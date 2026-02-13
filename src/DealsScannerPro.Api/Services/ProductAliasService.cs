using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class ProductAliasService : IProductAliasService
{
    private readonly TableClient _aliasesTable;
    private readonly TableClient _membersTable;
    private readonly IFuzzySearchService _fuzzyService;
    private readonly ILogger<ProductAliasService> _logger;

    // In-memory cache with expiration
    private static List<ProductAlias>? _cachedAliases;
    private static Dictionary<string, string>? _cachedReverseLookup; // "{retailer}_{skuKeyHash}" → aliasId
    private static DateTime _cacheExpiration = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly object _cacheLock = new();

    public ProductAliasService(
        IConfiguration configuration,
        IFuzzySearchService fuzzyService,
        ILogger<ProductAliasService> logger)
    {
        _fuzzyService = fuzzyService;
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _aliasesTable = serviceClient.GetTableClient("ProductAliases");
        _aliasesTable.CreateIfNotExists();
        _membersTable = serviceClient.GetTableClient("ProductAliasMembers");
        _membersTable.CreateIfNotExists();
    }

    // --- CRUD ---

    public async Task<List<ProductAlias>> GetAliasesAsync(bool includeInactive = false)
    {
        var aliases = await GetCachedAliasesAsync();

        return includeInactive
            ? aliases
            : aliases.Where(a => a.IsActive).ToList();
    }

    public async Task<ProductAlias?> GetAliasByIdAsync(string aliasId)
    {
        try
        {
            var response = await _aliasesTable.GetEntityAsync<ProductAlias>("aliases", aliasId);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<ProductAlias> CreateAliasAsync(CreateAliasRequest request)
    {
        var canonicalId = GenerateCanonicalId(request.CanonicalBrand, request.CanonicalProduct, request.CanonicalVariant);

        var alias = new ProductAlias
        {
            PartitionKey = "aliases",
            RowKey = canonicalId,
            CanonicalBrand = request.CanonicalBrand,
            CanonicalProduct = request.CanonicalProduct,
            CanonicalVariant = request.CanonicalVariant,
            CreatedBy = request.CreatedBy ?? "anonymous",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Create member entities for initial members
        var memberDtos = new List<ProductAliasMemberDto>();
        var keywordParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in request.InitialMembers)
        {
            var skuKeyHash = HashSkuKey(member.SkuKey);

            var memberEntity = new ProductAliasMember
            {
                PartitionKey = member.Retailer.ToLowerInvariant(),
                RowKey = skuKeyHash,
                AliasId = canonicalId,
                SkuKey = member.SkuKey,
                Retailer = member.Retailer.ToLowerInvariant(),
                BrandNorm = member.BrandNorm,
                ProductNorm = member.ProductNorm,
                VariantNorm = member.VariantNorm,
                AddedBy = member.AddedBy ?? request.CreatedBy ?? "anonymous",
                AddedAt = DateTime.UtcNow
            };

            await _membersTable.UpsertEntityAsync(memberEntity, TableUpdateMode.Replace);

            memberDtos.Add(new ProductAliasMemberDto
            {
                SkuKey = member.SkuKey,
                Retailer = member.Retailer.ToLowerInvariant(),
                BrandNorm = member.BrandNorm,
                ProductNorm = member.ProductNorm,
                VariantNorm = member.VariantNorm
            });

            AddKeywords(keywordParts, member.BrandNorm, member.ProductNorm, member.VariantNorm);
        }

        // Add canonical fields to keywords
        AddKeywords(keywordParts, request.CanonicalBrand, request.CanonicalProduct, request.CanonicalVariant);

        alias.MembersJson = JsonSerializer.Serialize(memberDtos);
        alias.MemberCount = memberDtos.Count;
        alias.Keywords = string.Join(",", keywordParts);

        await _aliasesTable.UpsertEntityAsync(alias, TableUpdateMode.Replace);
        ClearCache();

        _logger.LogInformation("Created product alias: {AliasId} with {MemberCount} members", canonicalId, memberDtos.Count);
        return alias;
    }

    public async Task<ProductAlias> AddMemberAsync(string aliasId, AddMemberRequest request)
    {
        // Verify alias exists
        var alias = await GetAliasByIdAsync(aliasId)
            ?? throw new InvalidOperationException($"Alias '{aliasId}' not found");

        var skuKeyHash = HashSkuKey(request.SkuKey);
        var retailer = request.Retailer.ToLowerInvariant();

        // Check if this SKU key is already in another alias group
        try
        {
            var existing = await _membersTable.GetEntityAsync<ProductAliasMember>(retailer, skuKeyHash);
            if (existing.Value.AliasId != aliasId)
            {
                throw new InvalidOperationException(
                    $"SKU key is already a member of alias '{existing.Value.AliasId}'");
            }
            // Already in this group — no-op, just return
            return alias;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Not in any group — proceed
        }

        var memberEntity = new ProductAliasMember
        {
            PartitionKey = retailer,
            RowKey = skuKeyHash,
            AliasId = aliasId,
            SkuKey = request.SkuKey,
            Retailer = retailer,
            BrandNorm = request.BrandNorm,
            ProductNorm = request.ProductNorm,
            VariantNorm = request.VariantNorm,
            AddedBy = request.AddedBy ?? "anonymous",
            AddedAt = DateTime.UtcNow
        };

        await _membersTable.UpsertEntityAsync(memberEntity, TableUpdateMode.Replace);

        // Rebuild alias metadata
        alias = await RebuildAliasMembers(aliasId, alias);
        ClearCache();

        _logger.LogInformation("Added member {Retailer}/{SkuKeyHash} to alias {AliasId}", retailer, skuKeyHash, aliasId);
        return alias;
    }

    public async Task<ProductAlias> RemoveMemberAsync(string aliasId, string skuKey, string retailer)
    {
        var alias = await GetAliasByIdAsync(aliasId)
            ?? throw new InvalidOperationException($"Alias '{aliasId}' not found");

        var skuKeyHash = HashSkuKey(skuKey);
        retailer = retailer.ToLowerInvariant();

        try
        {
            await _membersTable.DeleteEntityAsync(retailer, skuKeyHash);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already removed
        }

        // Rebuild alias metadata
        alias = await RebuildAliasMembers(aliasId, alias);

        // Deactivate if no members left
        if (alias.MemberCount == 0)
        {
            alias.IsActive = false;
            alias.UpdatedAt = DateTime.UtcNow;
            await _aliasesTable.UpsertEntityAsync(alias, TableUpdateMode.Replace);
        }

        ClearCache();

        _logger.LogInformation("Removed member {Retailer}/{SkuKey} from alias {AliasId}", retailer, skuKey, aliasId);
        return alias;
    }

    public async Task DeactivateAliasAsync(string aliasId)
    {
        var alias = await GetAliasByIdAsync(aliasId)
            ?? throw new InvalidOperationException($"Alias '{aliasId}' not found");

        alias.IsActive = false;
        alias.UpdatedAt = DateTime.UtcNow;
        await _aliasesTable.UpsertEntityAsync(alias, TableUpdateMode.Replace);
        ClearCache();

        _logger.LogInformation("Deactivated product alias: {AliasId}", aliasId);
    }

    // --- Lookup ---

    public async Task<ProductAlias?> ResolveAliasAsync(string skuKey, string retailer)
    {
        var skuKeyHash = HashSkuKey(skuKey);
        retailer = retailer.ToLowerInvariant();

        try
        {
            var member = await _membersTable.GetEntityAsync<ProductAliasMember>(retailer, skuKeyHash);
            return await GetAliasByIdAsync(member.Value.AliasId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Dictionary<string, string>> BatchResolveAsync(string retailer, List<string> skuKeys)
    {
        var result = new Dictionary<string, string>();
        var reverseLookup = await GetCachedReverseLookupAsync();
        retailer = retailer.ToLowerInvariant();

        foreach (var skuKey in skuKeys)
        {
            var skuKeyHash = HashSkuKey(skuKey);
            var lookupKey = $"{retailer}_{skuKeyHash}";

            if (reverseLookup.TryGetValue(lookupKey, out var aliasId))
            {
                result[skuKey] = aliasId;
            }
        }

        return result;
    }

    // --- Suggestions ---

    public async Task<List<AliasSuggestion>> SuggestMatchesAsync(string? brandNorm, string productNorm, string? variantNorm)
    {
        var aliases = await GetCachedAliasesAsync();
        var suggestions = new List<AliasSuggestion>();

        foreach (var alias in aliases.Where(a => a.IsActive))
        {
            double score = 0;
            var reasons = new List<string>();

            // Brand match: exact +0.3
            if (!string.IsNullOrEmpty(brandNorm) && !string.IsNullOrEmpty(alias.CanonicalBrand))
            {
                if (string.Equals(brandNorm, alias.CanonicalBrand, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.3;
                    reasons.Add("brand match");
                }
            }

            // Product match: exact +0.5, fuzzy +0.5 (scaled by fuzzy quality)
            if (!string.IsNullOrEmpty(productNorm) && !string.IsNullOrEmpty(alias.CanonicalProduct))
            {
                if (string.Equals(productNorm, alias.CanonicalProduct, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.5;
                    reasons.Add("exact product match");
                }
                else if (_fuzzyService.IsFuzzyMatch(alias.CanonicalProduct, productNorm))
                {
                    score += 0.3;
                    reasons.Add("fuzzy product match");
                }
            }

            // Variant match: +0.2
            if (!string.IsNullOrEmpty(variantNorm) && !string.IsNullOrEmpty(alias.CanonicalVariant))
            {
                if (string.Equals(variantNorm, alias.CanonicalVariant, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.2;
                    reasons.Add("variant match");
                }
            }

            if (score >= 0.3)
            {
                suggestions.Add(new AliasSuggestion
                {
                    AliasId = alias.RowKey,
                    CanonicalBrand = alias.CanonicalBrand,
                    CanonicalProduct = alias.CanonicalProduct,
                    CanonicalVariant = alias.CanonicalVariant,
                    MemberCount = alias.MemberCount,
                    Score = Math.Round(score, 2),
                    MatchReason = string.Join(", ", reasons)
                });
            }
        }

        return suggestions
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.MemberCount)
            .Take(10)
            .ToList();
    }

    public async Task<List<ProductAlias>> SearchAliasesAsync(string query)
    {
        var aliases = await GetCachedAliasesAsync();

        return aliases
            .Where(a => a.IsActive &&
                (_fuzzyService.IsFuzzyMatch(a.Keywords, query) ||
                 _fuzzyService.IsFuzzyMatch(a.CanonicalProduct, query) ||
                 _fuzzyService.IsFuzzyMatch(a.CanonicalBrand, query)))
            .ToList();
    }

    // --- Cache ---

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedAliases = null;
            _cachedReverseLookup = null;
            _cacheExpiration = DateTime.MinValue;
        }
        _logger.LogDebug("Product alias cache cleared");
    }

    // --- Private helpers ---

    private async Task<List<ProductAlias>> GetCachedAliasesAsync()
    {
        lock (_cacheLock)
        {
            if (_cachedAliases != null && DateTime.UtcNow < _cacheExpiration)
            {
                return _cachedAliases;
            }
        }

        var aliases = new List<ProductAlias>();
        await foreach (var entity in _aliasesTable.QueryAsync<ProductAlias>(a => a.PartitionKey == "aliases"))
        {
            aliases.Add(entity);
        }

        lock (_cacheLock)
        {
            _cachedAliases = aliases;
            if (DateTime.UtcNow >= _cacheExpiration)
            {
                _cacheExpiration = DateTime.UtcNow.Add(CacheDuration);
            }
        }

        _logger.LogDebug("Loaded {Count} product aliases from storage", aliases.Count);
        return aliases;
    }

    private async Task<Dictionary<string, string>> GetCachedReverseLookupAsync()
    {
        lock (_cacheLock)
        {
            if (_cachedReverseLookup != null && DateTime.UtcNow < _cacheExpiration)
            {
                return _cachedReverseLookup;
            }
        }

        var lookup = new Dictionary<string, string>();
        await foreach (var member in _membersTable.QueryAsync<ProductAliasMember>())
        {
            var key = $"{member.PartitionKey}_{member.RowKey}";
            lookup[key] = member.AliasId;
        }

        lock (_cacheLock)
        {
            _cachedReverseLookup = lookup;
            if (DateTime.UtcNow >= _cacheExpiration)
            {
                _cacheExpiration = DateTime.UtcNow.Add(CacheDuration);
            }
        }

        _logger.LogDebug("Built reverse lookup with {Count} entries", lookup.Count);
        return lookup;
    }

    private async Task<ProductAlias> RebuildAliasMembers(string aliasId, ProductAlias alias)
    {
        var members = new List<ProductAliasMember>();
        await foreach (var member in _membersTable.QueryAsync<ProductAliasMember>())
        {
            if (member.AliasId == aliasId)
            {
                members.Add(member);
            }
        }

        var memberDtos = members.Select(m => new ProductAliasMemberDto
        {
            SkuKey = m.SkuKey,
            Retailer = m.Retailer,
            BrandNorm = m.BrandNorm,
            ProductNorm = m.ProductNorm,
            VariantNorm = m.VariantNorm
        }).ToList();

        var keywordParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKeywords(keywordParts, alias.CanonicalBrand, alias.CanonicalProduct, alias.CanonicalVariant);
        foreach (var m in members)
        {
            AddKeywords(keywordParts, m.BrandNorm, m.ProductNorm, m.VariantNorm);
        }

        alias.MembersJson = JsonSerializer.Serialize(memberDtos);
        alias.MemberCount = memberDtos.Count;
        alias.Keywords = string.Join(",", keywordParts);
        alias.UpdatedAt = DateTime.UtcNow;

        await _aliasesTable.UpsertEntityAsync(alias, TableUpdateMode.Replace);
        return alias;
    }

    internal static string HashSkuKey(string skuKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(skuKey));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    internal static string GenerateCanonicalId(string brand, string product, string? variant)
    {
        var parts = new List<string>
        {
            SkuKeyGenerator.Normalize(brand) ?? "null",
            SkuKeyGenerator.Normalize(product) ?? "null",
            SkuKeyGenerator.Normalize(variant) ?? "null"
        };
        return string.Join("_", parts);
    }

    private static void AddKeywords(HashSet<string> keywords, string? brand, string? product, string? variant)
    {
        if (!string.IsNullOrWhiteSpace(brand))
        {
            foreach (var word in brand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                keywords.Add(word.ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(product))
        {
            foreach (var word in product.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                keywords.Add(word.ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(variant))
        {
            foreach (var word in variant.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                keywords.Add(word.ToLowerInvariant());
        }
    }
}
