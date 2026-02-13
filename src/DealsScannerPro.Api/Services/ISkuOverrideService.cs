using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface ISkuOverrideService
{
    Task<List<SkuOverride>> GetOverridesAsync(string retailer, string? overrideType = null);
    Task<SkuOverride?> GetOverrideAsync(string retailer, string overrideId);
    Task<SkuOverride> CreateOverrideAsync(SkuOverride skuOverride);
    Task DeactivateOverrideAsync(string retailer, string overrideId);
    Task<SkuOverrideApplicationResult> ApplyOverridesAsync(string retailer, List<Offer> offers);
}

public class SkuOverrideApplicationResult
{
    public int MatchesApplied { get; set; }
    public int SplitsApplied { get; set; }
    public int OffersModified { get; set; }
    public List<Offer> NewOffersFromSplits { get; set; } = new();
    public List<SkuOverrideApplicationDetail> Details { get; set; } = new();
}

public class SkuOverrideApplicationDetail
{
    public string OverrideId { get; set; } = string.Empty;
    public string OverrideType { get; set; } = string.Empty;
    public int HitCount { get; set; }
}
