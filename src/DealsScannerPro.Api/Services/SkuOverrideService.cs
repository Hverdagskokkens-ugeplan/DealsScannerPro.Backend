using System.Text.Json;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class SkuOverrideService : ISkuOverrideService
{
    private readonly TableClient _skuOverridesTable;
    private readonly ILogger<SkuOverrideService> _logger;

    public SkuOverrideService(IConfiguration configuration, ILogger<SkuOverrideService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _skuOverridesTable = serviceClient.GetTableClient("SkuOverrides");
        _skuOverridesTable.CreateIfNotExists();
    }

    public async Task<List<SkuOverride>> GetOverridesAsync(string retailer, string? overrideType = null)
    {
        var partitionKey = retailer.ToLowerInvariant();
        var overrides = new List<SkuOverride>();

        await foreach (var entity in _skuOverridesTable.QueryAsync<SkuOverride>(
            o => o.PartitionKey == partitionKey && o.IsActive))
        {
            if (overrideType == null || entity.OverrideType == overrideType)
            {
                overrides.Add(entity);
            }
        }

        _logger.LogDebug("Loaded {Count} overrides for {Retailer} (type={OverrideType})",
            overrides.Count, retailer, overrideType ?? "all");

        return overrides;
    }

    public async Task<SkuOverride?> GetOverrideAsync(string retailer, string overrideId)
    {
        var partitionKey = retailer.ToLowerInvariant();

        try
        {
            var response = await _skuOverridesTable.GetEntityAsync<SkuOverride>(partitionKey, overrideId);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<SkuOverride> CreateOverrideAsync(SkuOverride skuOverride)
    {
        // Validate based on type
        switch (skuOverride.OverrideType)
        {
            case OverrideTypes.Match:
                if (string.IsNullOrWhiteSpace(skuOverride.SkuA))
                    throw new ArgumentException("MATCH override requires non-empty SkuA");
                if (string.IsNullOrWhiteSpace(skuOverride.SkuB))
                    throw new ArgumentException("MATCH override requires non-empty SkuB");
                if (skuOverride.SkuA == skuOverride.SkuB)
                    throw new ArgumentException("MATCH override requires SkuA != SkuB");
                break;

            case OverrideTypes.Split:
                if (string.IsNullOrWhiteSpace(skuOverride.SkuA))
                    throw new ArgumentException("SPLIT override requires non-empty SkuA");
                if (string.IsNullOrWhiteSpace(skuOverride.SplitIntoJson))
                    throw new ArgumentException("SPLIT override requires SplitIntoJson");

                List<SplitTarget>? targets;
                try
                {
                    targets = JsonSerializer.Deserialize<List<SplitTarget>>(skuOverride.SplitIntoJson);
                }
                catch (JsonException)
                {
                    throw new ArgumentException("SPLIT override SplitIntoJson must be valid JSON array of SplitTarget");
                }

                if (targets == null || targets.Count < 2)
                    throw new ArgumentException("SPLIT override requires at least 2 split targets");
                break;

            default:
                throw new ArgumentException(
                    $"Invalid override type '{skuOverride.OverrideType}'. Must be '{OverrideTypes.Match}' or '{OverrideTypes.Split}'");
        }

        // Set keys
        skuOverride.PartitionKey = skuOverride.Retailer.ToLowerInvariant();
        skuOverride.RowKey = $"{skuOverride.OverrideType}_{Guid.NewGuid():N}";
        skuOverride.CreatedAt = DateTime.UtcNow;
        skuOverride.IsActive = true;

        await _skuOverridesTable.AddEntityAsync(skuOverride);

        _logger.LogInformation("Created SKU override {RowKey} for {Retailer}: {OverrideType} SkuA='{SkuA}'",
            skuOverride.RowKey, skuOverride.PartitionKey, skuOverride.OverrideType, skuOverride.SkuA);

        return skuOverride;
    }

    public async Task DeactivateOverrideAsync(string retailer, string overrideId)
    {
        var existing = await GetOverrideAsync(retailer, overrideId)
            ?? throw new InvalidOperationException($"Override '{overrideId}' not found for retailer '{retailer}'");

        existing.IsActive = false;

        await _skuOverridesTable.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace);

        _logger.LogInformation("Deactivated SKU override {RowKey} for {Retailer}", overrideId, retailer);
    }

    public async Task<SkuOverrideApplicationResult> ApplyOverridesAsync(string retailer, List<Offer> offers)
    {
        var result = new SkuOverrideApplicationResult();
        var overrides = await GetOverridesAsync(retailer);

        if (overrides.Count == 0)
        {
            _logger.LogDebug("No active SKU overrides for {Retailer}", retailer);
            return result;
        }

        var hitCounts = new Dictionary<string, (int count, string type)>();

        // Apply MATCH overrides first
        foreach (var ov in overrides.Where(o => o.OverrideType == OverrideTypes.Match))
        {
            foreach (var offer in offers)
            {
                if (offer.Status == OfferStatus.Deleted)
                    continue;

                if (offer.SkuKey == ov.SkuB)
                {
                    _logger.LogDebug("MATCH: Changing SkuKey from '{OldSku}' to '{NewSku}' for offer {RowKey}",
                        offer.SkuKey, ov.SkuA, offer.RowKey);

                    offer.SkuKey = ov.SkuA;
                    result.MatchesApplied++;
                    result.OffersModified++;

                    if (!hitCounts.ContainsKey(ov.RowKey))
                        hitCounts[ov.RowKey] = (0, ov.OverrideType);
                    hitCounts[ov.RowKey] = (hitCounts[ov.RowKey].count + 1, ov.OverrideType);
                }
            }
        }

        // Apply SPLIT overrides
        foreach (var ov in overrides.Where(o => o.OverrideType == OverrideTypes.Split))
        {
            List<SplitTarget>? targets;
            try
            {
                targets = JsonSerializer.Deserialize<List<SplitTarget>>(ov.SplitIntoJson!);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize SplitIntoJson for override {RowKey}", ov.RowKey);
                continue;
            }

            if (targets == null || targets.Count < 2)
                continue;

            // Iterate over a copy since we modify the collection
            var matchingOffers = offers.Where(o => o.Status != OfferStatus.Deleted && o.SkuKey == ov.SkuA).ToList();

            foreach (var offer in matchingOffers)
            {
                _logger.LogDebug("SPLIT: Splitting offer {RowKey} (SkuKey={SkuKey}) into {Count} targets",
                    offer.RowKey, offer.SkuKey, targets.Count);

                // Mark original as deleted
                offer.Status = OfferStatus.Deleted;
                offer.ReviewReason = $"Split by SKU override {ov.RowKey}";
                result.OffersModified++;

                // Create new offers from split targets
                foreach (var target in targets)
                {
                    var newOffer = CloneOfferWithTarget(offer, target);
                    result.NewOffersFromSplits.Add(newOffer);
                }

                result.SplitsApplied++;

                if (!hitCounts.ContainsKey(ov.RowKey))
                    hitCounts[ov.RowKey] = (0, ov.OverrideType);
                hitCounts[ov.RowKey] = (hitCounts[ov.RowKey].count + 1, ov.OverrideType);
            }
        }

        // Build details
        result.Details = hitCounts.Select(kvp => new SkuOverrideApplicationDetail
        {
            OverrideId = kvp.Key,
            OverrideType = kvp.Value.type,
            HitCount = kvp.Value.count
        }).ToList();

        _logger.LogInformation(
            "SKU overrides applied for {Retailer}: {Matches} matches, {Splits} splits, {Modified} modified, {NewOffers} new offers",
            retailer, result.MatchesApplied, result.SplitsApplied, result.OffersModified, result.NewOffersFromSplits.Count);

        return result;
    }

    private static Offer CloneOfferWithTarget(Offer parent, SplitTarget target)
    {
        // Inherit from parent, override with target values
        var productNorm = target.ProductNorm;
        var variantNorm = target.VariantNorm ?? parent.VariantNorm;
        var brandNorm = target.BrandNorm ?? parent.BrandNorm;
        var netAmountValue = target.NetAmountValue ?? parent.NetAmountValue;
        var netAmountUnit = target.NetAmountUnit ?? parent.NetAmountUnit;
        var containerType = target.ContainerType ?? parent.ContainerType;
        var priceValue = target.PriceValue ?? parent.PriceValue;

        // Generate SKU key if not provided
        var skuKey = target.SkuKey ?? SkuKeyGenerator.Generate(
            brandNorm, productNorm, variantNorm, containerType, netAmountValue, netAmountUnit);

        // Recalculate unit price
        var (unitPriceValue, unitPriceUnit) = UnitPriceCalculator.Calculate(
            priceValue, parent.DepositValue, netAmountValue, netAmountUnit, parent.PackCount);

        var priceExclDeposit = UnitPriceCalculator.CalculatePriceExclDeposit(priceValue, parent.DepositValue);

        return new Offer
        {
            PartitionKey = parent.PartitionKey,
            RowKey = skuKey ?? Guid.NewGuid().ToString("N"),
            Retailer = parent.Retailer,
            ValidFrom = parent.ValidFrom,
            ValidTo = parent.ValidTo,
            ProductTextRaw = parent.ProductTextRaw,
            BrandNorm = brandNorm,
            ProductNorm = productNorm,
            VariantNorm = variantNorm,
            Category = parent.Category,
            NetAmountValue = netAmountValue,
            NetAmountUnit = netAmountUnit,
            PackCount = parent.PackCount,
            ContainerType = containerType,
            PriceValue = priceValue,
            DepositValue = parent.DepositValue,
            PriceExclDeposit = priceExclDeposit,
            UnitPriceValue = unitPriceValue,
            UnitPriceUnit = unitPriceUnit,
            SkuKey = skuKey,
            Comment = $"Split from {parent.SkuKey} by SKU override",
            Confidence = parent.Confidence,
            ConfidenceDetailsJson = parent.ConfidenceDetailsJson,
            ConfidenceReasonsJson = parent.ConfidenceReasonsJson,
            CropUrl = parent.CropUrl,
            TraceJson = parent.TraceJson,
            CandidatesJson = parent.CandidatesJson,
            Status = OfferStatus.NeedsReview,
            ReviewReason = "Created from SKU split override",
            ScannerVersion = parent.ScannerVersion,
            CreatedAt = DateTime.UtcNow
        };
    }
}
