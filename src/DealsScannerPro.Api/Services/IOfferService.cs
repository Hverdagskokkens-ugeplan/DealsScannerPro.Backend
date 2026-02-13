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

    // Shopping list matching
    Task<ShoppingListResult> MatchShoppingListAsync(ShoppingListRequest request);

    // Review
    Task<Offer> UpdateOfferAsync(string partitionKey, string rowKey, OfferUpdate update, string reviewedBy);
    Task DeleteOfferAsync(string partitionKey, string rowKey, string deletedBy, string reason);
    Task BatchApproveAsync(List<string> offerIds, string approvedBy);

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

public class ShoppingListRequest
{
    public List<string> Items { get; set; } = new();
    public DateTime? Date { get; set; }
    public List<string>? Retailers { get; set; }
}

public class ShoppingListResult
{
    public List<ShoppingListItemResult> Results { get; set; } = new();
    public ShoppingListSummary Summary { get; set; } = new();
}

public class ShoppingListItemResult
{
    public string SearchTerm { get; set; } = string.Empty;
    public List<ShoppingListMatch> Matches { get; set; } = new();
}

public class ShoppingListMatch
{
    public string OfferId { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public string ProductNorm { get; set; } = string.Empty;
    public string? BrandNorm { get; set; }
    public string? VariantNorm { get; set; }
    public string ProductTextRaw { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double? PriceValue { get; set; }
    public double? UnitPriceValue { get; set; }
    public string? UnitPriceUnit { get; set; }
    public double? NetAmountValue { get; set; }
    public string? NetAmountUnit { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public double MatchScore { get; set; }
}

public class ShoppingListSummary
{
    public int TotalItems { get; set; }
    public int ItemsMatched { get; set; }
    public int ItemsNotFound { get; set; }
    public List<string> NotFound { get; set; } = new();
}
