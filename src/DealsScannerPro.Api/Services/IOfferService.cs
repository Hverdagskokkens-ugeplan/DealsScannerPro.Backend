using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface IOfferService
{
    // Upload
    Task<UploadResult> UploadOffersAsync(ScannerUploadRequest request);
    Task<SaveResult> SaveOffersAsync(ScannerUploadRequest request);

    // Query
    Task<List<Offer>> GetOffersAsync(OfferQuery query);
    Task<Offer?> GetOfferByIdAsync(string partitionKey, string rowKey);
    Task<List<Offer>> GetOffersForReviewAsync(int maxResults = 50);
    Task<List<Offer>> SearchOffersAsync(string? searchTerm, DateTime? date, List<string>? retailers);

    // Review
    Task<Offer> UpdateOfferAsync(string partitionKey, string rowKey, OfferUpdate update, string reviewedBy);
    Task DeleteOfferAsync(string partitionKey, string rowKey, string deletedBy, string reason);
    Task BatchApproveAsync(List<string> offerIds, string approvedBy);

    // SKU Overrides
    Task<SkuOverride> CreateSkuOverrideAsync(SkuOverride skuOverride);
    Task<List<SkuOverride>> GetSkuOverridesAsync(string retailer);

    // Corrections
    Task<List<CorrectionEvent>> GetCorrectionEventsAsync(string offerId);
}

public class OfferQuery
{
    public string? Retailer { get; set; }
    public string? Category { get; set; }
    public DateTime? ValidOn { get; set; }
    public string? Status { get; set; }
    public int MaxResults { get; set; } = 100;
}

public class OfferUpdate
{
    public string? BrandNorm { get; set; }
    public string? ProductNorm { get; set; }
    public string? VariantNorm { get; set; }
    public string? Category { get; set; }
    public double? NetAmountValue { get; set; }
    public string? NetAmountUnit { get; set; }
    public int? PackCount { get; set; }
    public string? ContainerType { get; set; }
    public double? PriceValue { get; set; }
    public double? DepositValue { get; set; }
    public string? Comment { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
}

public class UploadResult
{
    public int TotalOffers { get; set; }
    public int Published { get; set; }
    public int NeedsReview { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class SaveResult
{
    public int Saved { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
}
