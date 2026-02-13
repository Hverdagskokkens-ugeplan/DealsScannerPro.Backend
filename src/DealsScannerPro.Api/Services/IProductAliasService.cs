using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface IProductAliasService
{
    // CRUD
    Task<List<ProductAlias>> GetAliasesAsync(bool includeInactive = false);
    Task<ProductAlias?> GetAliasByIdAsync(string aliasId);
    Task<ProductAlias> CreateAliasAsync(CreateAliasRequest request);
    Task<ProductAlias> AddMemberAsync(string aliasId, AddMemberRequest request);
    Task<ProductAlias> RemoveMemberAsync(string aliasId, string skuKey, string retailer);
    Task DeactivateAliasAsync(string aliasId);

    // Lookup
    Task<ProductAlias?> ResolveAliasAsync(string skuKey, string retailer);
    Task<Dictionary<string, string>> BatchResolveAsync(string retailer, List<string> skuKeys);

    // Suggestions
    Task<List<AliasSuggestion>> SuggestMatchesAsync(string? brandNorm, string productNorm, string? variantNorm);
    Task<List<ProductAlias>> SearchAliasesAsync(string query);

    // Cache
    void ClearCache();
}

// --- Request/Result DTOs ---

public class CreateAliasRequest
{
    public string CanonicalBrand { get; set; } = string.Empty;
    public string CanonicalProduct { get; set; } = string.Empty;
    public string? CanonicalVariant { get; set; }
    public List<AddMemberRequest> InitialMembers { get; set; } = new();
    public string? CreatedBy { get; set; }
}

public class AddMemberRequest
{
    public string SkuKey { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public string? BrandNorm { get; set; }
    public string? ProductNorm { get; set; }
    public string? VariantNorm { get; set; }
    public string? AddedBy { get; set; }
}

public class AliasSuggestion
{
    public string AliasId { get; set; } = string.Empty;
    public string CanonicalBrand { get; set; } = string.Empty;
    public string CanonicalProduct { get; set; } = string.Empty;
    public string? CanonicalVariant { get; set; }
    public int MemberCount { get; set; }
    public double Score { get; set; }
    public string MatchReason { get; set; } = string.Empty;
}
