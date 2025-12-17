using System.Text.Json;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class OfferService : IOfferService
{
    private readonly TableClient _offersTable;
    private readonly TableClient _skuOverridesTable;
    private readonly TableClient _correctionsTable;
    private readonly ILogger<OfferService> _logger;
    private const double ConfidenceThreshold = 0.9;

    public OfferService(IConfiguration configuration, ILogger<OfferService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);

        _offersTable = serviceClient.GetTableClient("Offers");
        _skuOverridesTable = serviceClient.GetTableClient("SkuOverrides");
        _correctionsTable = serviceClient.GetTableClient("CorrectionEvents");

        // Ensure tables exist
        _offersTable.CreateIfNotExists();
        _skuOverridesTable.CreateIfNotExists();
        _correctionsTable.CreateIfNotExists();
    }

    public async Task<UploadResult> UploadOffersAsync(ScannerUploadRequest request)
    {
        var result = new UploadResult();
        var meta = request.Meta;

        // Validate required meta
        if (string.IsNullOrEmpty(meta.Retailer))
        {
            result.Errors.Add("Retailer is required");
            return result;
        }

        var retailer = meta.Retailer.ToLowerInvariant();
        var validFrom = DateTime.TryParse(meta.ValidFrom, out var vf)
            ? DateTime.SpecifyKind(vf, DateTimeKind.Utc)
            : DateTime.UtcNow;
        var validTo = DateTime.TryParse(meta.ValidTo, out var vt)
            ? DateTime.SpecifyKind(vt, DateTimeKind.Utc)
            : DateTime.UtcNow.AddDays(7);

        var partitionKey = $"{retailer}_{validFrom:yyyy-MM-dd}_{validTo:yyyy-MM-dd}";

        foreach (var scannedOffer in request.Offers)
        {
            try
            {
                // Generate SKU key
                var skuKey = SkuKeyGenerator.Generate(
                    scannedOffer.BrandNorm,
                    scannedOffer.ProductNorm,
                    scannedOffer.VariantNorm,
                    scannedOffer.ContainerType,
                    scannedOffer.NetAmountValue,
                    scannedOffer.NetAmountUnit);

                // Calculate unit price
                var (unitPriceValue, unitPriceUnit) = UnitPriceCalculator.Calculate(
                    scannedOffer.PriceValue,
                    scannedOffer.DepositValue,
                    scannedOffer.NetAmountValue,
                    scannedOffer.NetAmountUnit,
                    scannedOffer.PackCount);

                // Calculate price excl deposit
                var priceExclDeposit = UnitPriceCalculator.CalculatePriceExclDeposit(
                    scannedOffer.PriceValue,
                    scannedOffer.DepositValue);

                // Determine status based on confidence
                var status = scannedOffer.Confidence >= ConfidenceThreshold
                    ? OfferStatus.Published
                    : OfferStatus.NeedsReview;

                // Build review reason if needed
                string? reviewReason = null;
                if (status == OfferStatus.NeedsReview)
                {
                    reviewReason = BuildReviewReason(scannedOffer, skuKey);
                }

                // Use SKU key as RowKey for dedup, fallback to hash
                var rowKey = skuKey ?? GenerateRowKeyFallback(scannedOffer);

                var offer = new Offer
                {
                    PartitionKey = partitionKey,
                    RowKey = rowKey,
                    Retailer = retailer,
                    ValidFrom = validFrom,
                    ValidTo = validTo,
                    ProductTextRaw = scannedOffer.ProductTextRaw,
                    BrandNorm = scannedOffer.BrandNorm,
                    ProductNorm = scannedOffer.ProductNorm,
                    VariantNorm = scannedOffer.VariantNorm,
                    Category = scannedOffer.Category,
                    NetAmountValue = scannedOffer.NetAmountValue,
                    NetAmountUnit = scannedOffer.NetAmountUnit,
                    PackCount = scannedOffer.PackCount,
                    ContainerType = scannedOffer.ContainerType,
                    PriceValue = scannedOffer.PriceValue,
                    DepositValue = scannedOffer.DepositValue,
                    PriceExclDeposit = priceExclDeposit,
                    UnitPriceValue = unitPriceValue,
                    UnitPriceUnit = unitPriceUnit,
                    SkuKey = skuKey,
                    Comment = scannedOffer.Comment,
                    Confidence = scannedOffer.Confidence,
                    ConfidenceDetailsJson = scannedOffer.ConfidenceDetails != null
                        ? JsonSerializer.Serialize(scannedOffer.ConfidenceDetails)
                        : null,
                    TraceJson = scannedOffer.Trace != null
                        ? JsonSerializer.Serialize(new OfferTrace
                        {
                            Page = scannedOffer.Trace.Page,
                            Bbox = scannedOffer.Trace.Bbox,
                            TextLines = scannedOffer.Trace.TextLines,
                            SourceFile = meta.SourceFile
                        })
                        : null,
                    Status = status,
                    ReviewReason = reviewReason,
                    CreatedAt = DateTime.UtcNow
                };

                await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);

                result.TotalOffers++;
                if (status == OfferStatus.Published)
                    result.Published++;
                else
                    result.NeedsReview++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload offer: {Product}", scannedOffer.ProductTextRaw);
                result.Errors.Add($"Failed to upload: {scannedOffer.ProductTextRaw}");
            }
        }

        _logger.LogInformation(
            "Uploaded {Total} offers for {Retailer} ({Published} published, {Review} for review)",
            result.TotalOffers, retailer, result.Published, result.NeedsReview);

        return result;
    }

    /// <summary>
    /// Save offers from scanner v2.0 - uses pre-calculated values from scanner.
    /// </summary>
    public async Task<SaveResult> SaveOffersAsync(ScannerUploadRequest request)
    {
        var result = new SaveResult();
        var meta = request.Meta;

        if (string.IsNullOrEmpty(meta.Retailer))
        {
            result.ErrorMessages.Add("Retailer is required");
            return result;
        }

        var retailer = meta.Retailer.ToLowerInvariant();
        var validFrom = DateTime.TryParse(meta.ValidFrom, out var vf)
            ? DateTime.SpecifyKind(vf, DateTimeKind.Utc)
            : DateTime.UtcNow;
        var validTo = DateTime.TryParse(meta.ValidTo, out var vt)
            ? DateTime.SpecifyKind(vt, DateTimeKind.Utc)
            : DateTime.UtcNow.AddDays(7);

        var partitionKey = $"{retailer}_{validFrom:yyyy-MM-dd}_{validTo:yyyy-MM-dd}";

        foreach (var scannedOffer in request.Offers)
        {
            try
            {
                // Use scanner's pre-calculated SKU key, or generate if missing
                var skuKey = scannedOffer.SkuKey ?? SkuKeyGenerator.Generate(
                    scannedOffer.BrandNorm,
                    scannedOffer.ProductNorm,
                    scannedOffer.VariantNorm,
                    scannedOffer.ContainerType,
                    scannedOffer.NetAmountValue,
                    scannedOffer.NetAmountUnit);

                // Use scanner's pre-calculated unit price, or calculate if missing
                var unitPriceValue = scannedOffer.UnitPriceValue;
                var unitPriceUnit = scannedOffer.UnitPriceUnit;

                if (!unitPriceValue.HasValue && scannedOffer.PriceValue.HasValue)
                {
                    (unitPriceValue, unitPriceUnit) = UnitPriceCalculator.Calculate(
                        scannedOffer.PriceValue,
                        scannedOffer.DepositValue,
                        scannedOffer.NetAmountValue,
                        scannedOffer.NetAmountUnit,
                        scannedOffer.PackCount);
                }

                // Use scanner's pre-calculated price excl deposit
                var priceExclDeposit = scannedOffer.PriceExclDeposit
                    ?? UnitPriceCalculator.CalculatePriceExclDeposit(
                        scannedOffer.PriceValue,
                        scannedOffer.DepositValue);

                // Map scanner status to our status
                var status = scannedOffer.Status?.ToLowerInvariant() switch
                {
                    "published" => OfferStatus.Published,
                    "low_confidence" => OfferStatus.LowConfidence,
                    _ => OfferStatus.NeedsReview
                };

                // Use SKU key as RowKey for dedup
                var rowKey = skuKey ?? GenerateRowKeyFallback(scannedOffer);

                var offer = new Offer
                {
                    PartitionKey = partitionKey,
                    RowKey = rowKey,
                    Retailer = retailer,
                    ValidFrom = validFrom,
                    ValidTo = validTo,
                    ProductTextRaw = scannedOffer.ProductTextRaw,
                    BrandNorm = scannedOffer.BrandNorm,
                    ProductNorm = scannedOffer.ProductNorm,
                    VariantNorm = scannedOffer.VariantNorm,
                    Category = scannedOffer.Category,
                    NetAmountValue = scannedOffer.NetAmountValue,
                    NetAmountUnit = scannedOffer.NetAmountUnit,
                    PackCount = scannedOffer.PackCount,
                    ContainerType = scannedOffer.ContainerType,
                    PriceValue = scannedOffer.PriceValue,
                    DepositValue = scannedOffer.DepositValue,
                    PriceExclDeposit = priceExclDeposit,
                    UnitPriceValue = unitPriceValue,
                    UnitPriceUnit = unitPriceUnit,
                    SkuKey = skuKey,
                    Comment = scannedOffer.Comment,
                    Confidence = scannedOffer.Confidence,
                    ConfidenceDetailsJson = scannedOffer.ConfidenceDetails != null
                        ? JsonSerializer.Serialize(scannedOffer.ConfidenceDetails)
                        : null,
                    ConfidenceReasonsJson = scannedOffer.ConfidenceReasons != null
                        ? JsonSerializer.Serialize(scannedOffer.ConfidenceReasons)
                        : null,
                    CropUrl = scannedOffer.CropUrl,
                    TraceJson = scannedOffer.Trace != null
                        ? JsonSerializer.Serialize(scannedOffer.Trace)
                        : null,
                    Status = status,
                    ReviewReason = scannedOffer.ConfidenceReasons != null
                        ? string.Join("; ", scannedOffer.ConfidenceReasons)
                        : null,
                    CreatedAt = DateTime.UtcNow,
                    ScannerVersion = request.ScanStats?.ScannerVersion ?? request.Version
                };

                await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);
                result.Saved++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save offer: {Product}", scannedOffer.ProductTextRaw);
                result.Errors++;
                result.ErrorMessages.Add($"Failed: {scannedOffer.ProductTextRaw?.Substring(0, Math.Min(50, scannedOffer.ProductTextRaw?.Length ?? 0))}");
            }
        }

        _logger.LogInformation(
            "SaveOffersAsync complete: saved={Saved}, skipped={Skipped}, errors={Errors}",
            result.Saved, result.Skipped, result.Errors);

        return result;
    }

    public async Task<List<Offer>> GetOffersAsync(OfferQuery query)
    {
        var results = new List<Offer>();
        var filters = new List<string>();

        if (!string.IsNullOrEmpty(query.Retailer))
            filters.Add($"Retailer eq '{query.Retailer.ToLowerInvariant()}'");

        if (!string.IsNullOrEmpty(query.Category))
            filters.Add($"Category eq '{query.Category}'");

        if (!string.IsNullOrEmpty(query.Status))
            filters.Add($"Status eq '{query.Status}'");

        if (query.ValidOn.HasValue)
        {
            var dateStr = query.ValidOn.Value.ToString("yyyy-MM-dd");
            filters.Add($"ValidFrom le datetime'{dateStr}T00:00:00Z'");
            filters.Add($"ValidTo ge datetime'{dateStr}T00:00:00Z'");
        }

        var filter = filters.Count > 0 ? string.Join(" and ", filters) : null;

        var queryResult = filter != null
            ? _offersTable.QueryAsync<Offer>(filter, maxPerPage: query.MaxResults)
            : _offersTable.QueryAsync<Offer>(maxPerPage: query.MaxResults);

        await foreach (var offer in queryResult)
        {
            results.Add(offer);
            if (results.Count >= query.MaxResults) break;
        }

        return results;
    }

    public async Task<Offer?> GetOfferByIdAsync(string partitionKey, string rowKey)
    {
        try
        {
            var response = await _offersTable.GetEntityAsync<Offer>(partitionKey, rowKey);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<List<Offer>> GetOffersForReviewAsync(int maxResults = 50)
    {
        var results = new List<Offer>();

        await foreach (var offer in _offersTable.QueryAsync<Offer>(
            $"Status eq '{OfferStatus.NeedsReview}'",
            maxPerPage: maxResults))
        {
            results.Add(offer);
            if (results.Count >= maxResults) break;
        }

        // Sort by confidence (lowest first)
        return results.OrderBy(o => o.Confidence).ToList();
    }

    public async Task<List<Offer>> SearchOffersAsync(string? searchTerm, DateTime? date, List<string>? retailers)
    {
        var query = new OfferQuery
        {
            ValidOn = date ?? DateTime.Today,
            Status = OfferStatus.Published,
            MaxResults = 500
        };

        var offers = await GetOffersAsync(query);

        // Filter by retailers
        if (retailers?.Count > 0)
        {
            var retailerSet = retailers.Select(r => r.ToLowerInvariant()).ToHashSet();
            offers = offers.Where(o => retailerSet.Contains(o.Retailer)).ToList();
        }

        // Search filter
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.ToLowerInvariant();
            offers = offers.Where(o =>
                (o.ProductTextRaw?.ToLowerInvariant().Contains(term) ?? false) ||
                (o.BrandNorm?.ToLowerInvariant().Contains(term) ?? false) ||
                (o.ProductNorm?.ToLowerInvariant().Contains(term) ?? false) ||
                (o.Category?.ToLowerInvariant().Contains(term) ?? false)
            ).ToList();
        }

        // Sort by unit price
        return offers.OrderBy(o => o.UnitPriceValue ?? double.MaxValue).ToList();
    }

    public async Task<Offer> UpdateOfferAsync(string partitionKey, string rowKey, OfferUpdate update, string reviewedBy)
    {
        var offer = await GetOfferByIdAsync(partitionKey, rowKey)
            ?? throw new InvalidOperationException("Offer not found");

        var corrections = new List<CorrectionEvent>();

        // Track field changes
        if (update.BrandNorm != null && update.BrandNorm != offer.BrandNorm)
        {
            corrections.Add(CreateCorrectionEvent(offer, "BrandNorm", offer.BrandNorm, update.BrandNorm, reviewedBy, update.Reason));
            offer.BrandNorm = update.BrandNorm;
        }

        if (update.ProductNorm != null && update.ProductNorm != offer.ProductNorm)
        {
            corrections.Add(CreateCorrectionEvent(offer, "ProductNorm", offer.ProductNorm, update.ProductNorm, reviewedBy, update.Reason));
            offer.ProductNorm = update.ProductNorm;
        }

        if (update.VariantNorm != null && update.VariantNorm != offer.VariantNorm)
        {
            corrections.Add(CreateCorrectionEvent(offer, "VariantNorm", offer.VariantNorm, update.VariantNorm, reviewedBy, update.Reason));
            offer.VariantNorm = update.VariantNorm;
        }

        if (update.Category != null && update.Category != offer.Category)
        {
            corrections.Add(CreateCorrectionEvent(offer, "Category", offer.Category, update.Category, reviewedBy, update.Reason));
            offer.Category = update.Category;
        }

        if (update.PriceValue.HasValue && update.PriceValue != offer.PriceValue)
        {
            corrections.Add(CreateCorrectionEvent(offer, "PriceValue", offer.PriceValue?.ToString(), update.PriceValue.ToString(), reviewedBy, update.Reason));
            offer.PriceValue = update.PriceValue;
        }

        if (update.DepositValue.HasValue && update.DepositValue != offer.DepositValue)
        {
            corrections.Add(CreateCorrectionEvent(offer, "DepositValue", offer.DepositValue?.ToString(), update.DepositValue.ToString(), reviewedBy, update.Reason));
            offer.DepositValue = update.DepositValue;
        }

        if (update.NetAmountValue.HasValue)
            offer.NetAmountValue = update.NetAmountValue;
        if (update.NetAmountUnit != null)
            offer.NetAmountUnit = update.NetAmountUnit;
        if (update.PackCount.HasValue)
            offer.PackCount = update.PackCount;
        if (update.ContainerType != null)
            offer.ContainerType = update.ContainerType;
        if (update.Comment != null)
            offer.Comment = update.Comment;

        // Recalculate derived fields
        offer.SkuKey = SkuKeyGenerator.Generate(
            offer.BrandNorm, offer.ProductNorm, offer.VariantNorm,
            offer.ContainerType, offer.NetAmountValue, offer.NetAmountUnit);

        var (unitPrice, unitPriceUnit) = UnitPriceCalculator.Calculate(
            offer.PriceValue, offer.DepositValue,
            offer.NetAmountValue, offer.NetAmountUnit, offer.PackCount);
        offer.UnitPriceValue = unitPrice;
        offer.UnitPriceUnit = unitPriceUnit;
        offer.PriceExclDeposit = UnitPriceCalculator.CalculatePriceExclDeposit(offer.PriceValue, offer.DepositValue);

        // Update status
        if (update.Status != null && update.Status != offer.Status)
        {
            corrections.Add(new CorrectionEvent
            {
                PartitionKey = $"{partitionKey}|{rowKey}",
                RowKey = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{CorrectionEventTypes.StatusChange}",
                EventType = CorrectionEventTypes.StatusChange,
                OfferId = $"{partitionKey}|{rowKey}",
                OldStatus = offer.Status,
                NewStatus = update.Status,
                Reason = update.Reason,
                CorrectedBy = reviewedBy,
                CorrectedAt = DateTime.UtcNow
            });
            offer.Status = update.Status;
        }

        offer.ReviewedAt = DateTime.UtcNow;
        offer.ReviewedBy = reviewedBy;

        // Save offer and corrections
        await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);

        foreach (var correction in corrections)
        {
            await _correctionsTable.AddEntityAsync(correction);
        }

        return offer;
    }

    public async Task DeleteOfferAsync(string partitionKey, string rowKey, string deletedBy, string reason)
    {
        var offer = await GetOfferByIdAsync(partitionKey, rowKey)
            ?? throw new InvalidOperationException("Offer not found");

        // Log deletion
        var correction = new CorrectionEvent
        {
            PartitionKey = $"{partitionKey}|{rowKey}",
            RowKey = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{CorrectionEventTypes.Deleted}",
            EventType = CorrectionEventTypes.Deleted,
            OfferId = $"{partitionKey}|{rowKey}",
            OldStatus = offer.Status,
            NewStatus = OfferStatus.Deleted,
            Reason = reason,
            CorrectedBy = deletedBy,
            CorrectedAt = DateTime.UtcNow
        };

        offer.Status = OfferStatus.Deleted;
        offer.ReviewedAt = DateTime.UtcNow;
        offer.ReviewedBy = deletedBy;

        await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);
        await _correctionsTable.AddEntityAsync(correction);
    }

    public async Task BatchApproveAsync(List<string> offerIds, string approvedBy)
    {
        foreach (var offerId in offerIds)
        {
            var parts = offerId.Split('|');
            if (parts.Length != 2) continue;

            await UpdateOfferAsync(parts[0], parts[1], new OfferUpdate
            {
                Status = OfferStatus.Published,
                Reason = "Batch approved"
            }, approvedBy);
        }
    }

    public async Task<SkuOverride> CreateSkuOverrideAsync(SkuOverride skuOverride)
    {
        skuOverride.PartitionKey = skuOverride.Retailer.ToLowerInvariant();
        skuOverride.RowKey = $"{skuOverride.OverrideType}_{Guid.NewGuid():N}";

        await _skuOverridesTable.AddEntityAsync(skuOverride);
        return skuOverride;
    }

    public async Task<List<SkuOverride>> GetSkuOverridesAsync(string retailer)
    {
        var results = new List<SkuOverride>();

        await foreach (var entity in _skuOverridesTable.QueryAsync<SkuOverride>(
            $"PartitionKey eq '{retailer.ToLowerInvariant()}' and IsActive eq true"))
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task<List<CorrectionEvent>> GetCorrectionEventsAsync(string offerId)
    {
        var results = new List<CorrectionEvent>();

        await foreach (var entity in _correctionsTable.QueryAsync<CorrectionEvent>(
            $"PartitionKey eq '{offerId}'"))
        {
            results.Add(entity);
        }

        return results.OrderByDescending(c => c.CorrectedAt).ToList();
    }

    private static string BuildReviewReason(ScannedOffer offer, string? skuKey)
    {
        var reasons = new List<string>();

        if (string.IsNullOrEmpty(skuKey))
            reasons.Add("Could not generate SKU key");
        if (!offer.PriceValue.HasValue)
            reasons.Add("Missing price");
        if (string.IsNullOrEmpty(offer.ProductNorm))
            reasons.Add("Missing normalized product name");

        // Check confidence details if available
        if (offer.ConfidenceDetails != null)
        {
            if (offer.ConfidenceDetails.TryGetValue("price", out var priceConf) && priceConf < 0.9)
                reasons.Add("Low price confidence");
            if (offer.ConfidenceDetails.TryGetValue("amount", out var amountConf) && amountConf < 0.9)
                reasons.Add("Low amount confidence");
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : "Low overall confidence";
    }

    private static string GenerateRowKeyFallback(ScannedOffer offer)
    {
        var input = $"{offer.ProductTextRaw}|{offer.PriceValue}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static CorrectionEvent CreateCorrectionEvent(Offer offer, string field, string? oldValue, string? newValue, string correctedBy, string? reason)
    {
        return new CorrectionEvent
        {
            PartitionKey = $"{offer.PartitionKey}|{offer.RowKey}",
            RowKey = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{CorrectionEventTypes.FieldCorrection}_{field}",
            EventType = CorrectionEventTypes.FieldCorrection,
            OfferId = $"{offer.PartitionKey}|{offer.RowKey}",
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            Reason = reason,
            CorrectedBy = correctedBy,
            CorrectedAt = DateTime.UtcNow
        };
    }
}
