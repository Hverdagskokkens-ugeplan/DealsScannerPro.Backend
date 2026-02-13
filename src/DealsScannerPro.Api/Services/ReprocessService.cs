using System.Text.RegularExpressions;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class ReprocessService : IReprocessService
{
    private readonly TableClient _offersTable;
    private readonly IRetailerRuleService _ruleService;
    private readonly ISkuOverrideService _skuOverrideService;
    private readonly IProcessingRunService _runService;
    private readonly ILogger<ReprocessService> _logger;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private const double ConfidenceThreshold = 0.9;

    public ReprocessService(
        IRetailerRuleService ruleService,
        ISkuOverrideService skuOverrideService,
        IProcessingRunService runService,
        IConfiguration configuration,
        ILogger<ReprocessService> logger)
    {
        _ruleService = ruleService;
        _skuOverrideService = skuOverrideService;
        _runService = runService;
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _offersTable = serviceClient.GetTableClient("Offers");
        _offersTable.CreateIfNotExists();
    }

    public async Task<ReprocessResult> ReprocessAsync(string flyerId, string triggeredBy, string? reason)
    {
        // 1. Parse retailer from flyerId (e.g. "netto_2025-12-22_2025-12-28" → "netto")
        var retailer = ExtractRetailer(flyerId);

        // Create processing run record
        var processingRun = await _runService.CreateRunAsync(new CreateRunRequest
        {
            FlyerId = flyerId,
            Retailer = retailer,
            TriggerType = "reprocess",
            TriggeredBy = triggeredBy
        });
        var runId = processingRun.RunId;

        _logger.LogInformation("Reprocess started: RunId={RunId}, FlyerId={FlyerId}, TriggeredBy={TriggeredBy}",
            runId, flyerId, triggeredBy);

        try
        {

        // 2. Query all offers in the partition
        var offers = new List<Offer>();
        await foreach (var offer in _offersTable.QueryAsync<Offer>(
            o => o.PartitionKey == flyerId))
        {
            offers.Add(offer);
        }

        if (offers.Count == 0)
        {
            throw new InvalidOperationException($"No offers found for flyer '{flyerId}'");
        }

        _logger.LogInformation("Found {Count} offers for {FlyerId}", offers.Count, flyerId);

        // 3. Snapshot before metrics
        var before = SnapshotMetrics(offers);

        // 4. Fetch active rules for the retailer
        var rules = await _ruleService.GetRulesAsync(retailer);
        _logger.LogInformation("Loaded {Count} active rules for {Retailer}", rules.Count, retailer);

        // 5. Apply rules to each offer
        var ruleHitCounts = new Dictionary<string, int>();
        var modifiedOffers = new List<Offer>();
        var errorCount = 0;
        var skippedCount = 0;

        foreach (var offer in offers)
        {
            try
            {
                var wasModified = false;

                foreach (var rule in rules)
                {
                    if (!IsRegexMatch(rule.Pattern, offer.ProductTextRaw))
                        continue;

                    // Track hit
                    if (!ruleHitCounts.ContainsKey(rule.RowKey))
                        ruleHitCounts[rule.RowKey] = 0;
                    ruleHitCounts[rule.RowKey]++;

                    switch (rule.RuleType)
                    {
                        case RuleTypes.SkipPattern:
                            if (offer.Status != OfferStatus.Deleted)
                            {
                                offer.Status = OfferStatus.Deleted;
                                offer.ReviewReason = $"Reprocess: skip_pattern match (rule {rule.RowKey})";
                                wasModified = true;
                                skippedCount++;
                            }
                            break;

                        case RuleTypes.CategoryMap:
                            if (!string.IsNullOrEmpty(rule.TargetValue) && offer.Category != rule.TargetValue)
                            {
                                offer.Category = rule.TargetValue;
                                wasModified = true;
                            }
                            break;

                        case RuleTypes.PricePattern:
                            if (rule.Action == "set" && !string.IsNullOrEmpty(rule.TargetValue)
                                && double.TryParse(rule.TargetValue, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var newPrice))
                            {
                                if (offer.PriceValue != newPrice)
                                {
                                    offer.PriceValue = newPrice;
                                    wasModified = true;
                                }
                            }
                            break;

                        case RuleTypes.AmountPattern:
                            if (!string.IsNullOrEmpty(rule.TargetValue))
                            {
                                var parsed = ParseAmountValue(rule.TargetValue);
                                if (parsed.HasValue)
                                {
                                    if (offer.NetAmountValue != parsed.Value.value || offer.NetAmountUnit != parsed.Value.unit)
                                    {
                                        offer.NetAmountValue = parsed.Value.value;
                                        offer.NetAmountUnit = parsed.Value.unit;
                                        wasModified = true;
                                    }
                                }
                            }
                            break;
                    }
                }

                if (wasModified)
                {
                    // Recalculate derived fields
                    offer.SkuKey = SkuKeyGenerator.Generate(
                        offer.BrandNorm, offer.ProductNorm, offer.VariantNorm,
                        offer.ContainerType, offer.NetAmountValue, offer.NetAmountUnit);

                    var (unitPrice, unitPriceUnit) = UnitPriceCalculator.Calculate(
                        offer.PriceValue, offer.DepositValue,
                        offer.NetAmountValue, offer.NetAmountUnit, offer.PackCount);
                    offer.UnitPriceValue = unitPrice;
                    offer.UnitPriceUnit = unitPriceUnit;

                    offer.PriceExclDeposit = UnitPriceCalculator.CalculatePriceExclDeposit(
                        offer.PriceValue, offer.DepositValue);

                    offer.ReviewedAt = DateTime.UtcNow;
                    offer.ReviewedBy = triggeredBy;

                    modifiedOffers.Add(offer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error applying rules to offer {RowKey}", offer.RowKey);
                errorCount++;
            }
        }

        // 7. Upsert modified offers
        foreach (var offer in modifiedOffers)
        {
            await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);
        }

        // 8. Increment hit counts on rules that matched
        foreach (var (ruleId, hitCount) in ruleHitCounts)
        {
            for (var i = 0; i < hitCount; i++)
            {
                await _ruleService.IncrementHitCountAsync(retailer, ruleId);
            }
        }

        // 8b. Apply SKU overrides
        var overrideResult = await _skuOverrideService.ApplyOverridesAsync(retailer, offers);

        // Handle MATCH RowKey changes (SkuKey changed but RowKey hasn't)
        foreach (var offer in offers.Where(o => o.SkuKey != null && o.SkuKey != o.RowKey && o.Status != OfferStatus.Deleted))
        {
            // Soft-delete old entity
            var oldOffer = await _offersTable.GetEntityAsync<Offer>(offer.PartitionKey, offer.RowKey);
            if (oldOffer?.Value != null)
            {
                var toDelete = oldOffer.Value;
                toDelete.Status = OfferStatus.Deleted;
                toDelete.ReviewReason = "Replaced by SKU MATCH override";
                await _offersTable.UpsertEntityAsync(toDelete, TableUpdateMode.Replace);
            }

            // Upsert as new entity with updated RowKey
            offer.RowKey = offer.SkuKey!;
            await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);
        }

        // Handle SPLIT new offers
        foreach (var newOffer in overrideResult.NewOffersFromSplits)
        {
            await _offersTable.UpsertEntityAsync(newOffer, TableUpdateMode.Replace);
        }

        // Persist offers marked deleted by splits
        foreach (var offer in offers.Where(o => o.Status == OfferStatus.Deleted && o.ReviewReason != null && o.ReviewReason.Contains("Split by SKU override")))
        {
            await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);
        }

        // Add split offers to offers list for snapshot
        offers.AddRange(overrideResult.NewOffersFromSplits);

        // 9. Snapshot after metrics (re-read modified offers in memory)
        var after = SnapshotMetrics(offers);

        var result = new ReprocessResult
        {
            ProcessingRunId = runId,
            FlyerId = flyerId,
            Retailer = retailer,
            TotalOffers = offers.Count,
            RulesApplied = ruleHitCounts.Values.Sum(),
            OffersModified = modifiedOffers.Count,
            OffersSkipped = skippedCount,
            Errors = errorCount,
            Before = before,
            After = after,
            Improvement = new ImprovementMetrics
            {
                HighConfidenceDelta = after.HighConfidence - before.HighConfidence,
                AvgConfidenceDelta = Math.Round(after.AvgConfidence - before.AvgConfidence, 4)
            },
            RulesAppliedDetails = ruleHitCounts.Select(kvp =>
            {
                var rule = rules.FirstOrDefault(r => r.RowKey == kvp.Key);
                return new RuleApplicationDetail
                {
                    RuleId = kvp.Key,
                    RuleType = rule?.RuleType ?? "unknown",
                    HitCount = kvp.Value
                };
            }).ToList(),
            SkuMatchesApplied = overrideResult.MatchesApplied,
            SkuSplitsApplied = overrideResult.SplitsApplied,
            SkuOverrideOffersModified = overrideResult.OffersModified,
            SkuOverridesAppliedDetails = overrideResult.Details
        };

        _logger.LogInformation(
            "Reprocess complete: RunId={RunId}, Modified={Modified}, Skipped={Skipped}, Errors={Errors}",
            runId, modifiedOffers.Count, skippedCount, errorCount);

        // Complete processing run with metrics
        var activeOffers = offers.Where(o => o.Status != OfferStatus.Deleted).ToList();
        await _runService.CompleteRunAsync(flyerId, runId, new CompleteRunRequest
        {
            Metrics = new Models.RunMetrics
            {
                TotalOffers = activeOffers.Count,
                HighConfidence = activeOffers.Count(o => o.Confidence >= 0.9),
                MediumConfidence = activeOffers.Count(o => o.Confidence >= 0.7 && o.Confidence < 0.9),
                LowConfidence = activeOffers.Count(o => o.Confidence < 0.7),
                AvgConfidence = activeOffers.Count > 0
                    ? Math.Round(activeOffers.Average(o => o.Confidence), 4)
                    : 0,
                ByCategory = activeOffers
                    .GroupBy(o => o.Category ?? "Andet")
                    .ToDictionary(g => g.Key, g => g.Count())
            },
            OffersCreated = 0,
            OffersUpdated = modifiedOffers.Count
        });

        return result;
        }
        catch (Exception ex)
        {
            await _runService.FailRunAsync(flyerId, runId, ex.Message);
            throw;
        }
    }

    private static string ExtractRetailer(string flyerId)
    {
        var firstUnderscore = flyerId.IndexOf('_');
        if (firstUnderscore <= 0)
            throw new ArgumentException($"Invalid flyer ID format: '{flyerId}'. Expected format: retailer_YYYY-MM-DD_YYYY-MM-DD");

        return flyerId[..firstUnderscore].ToLowerInvariant();
    }

    private static MetricSnapshot SnapshotMetrics(List<Offer> offers)
    {
        return new MetricSnapshot
        {
            HighConfidence = offers.Count(o => o.Confidence >= ConfidenceThreshold && o.Status != OfferStatus.Deleted),
            LowConfidence = offers.Count(o => o.Confidence < ConfidenceThreshold && o.Status != OfferStatus.Deleted),
            AvgConfidence = offers.Count > 0
                ? Math.Round(offers.Where(o => o.Status != OfferStatus.Deleted).Select(o => o.Confidence).DefaultIfEmpty(0).Average(), 4)
                : 0,
            Published = offers.Count(o => o.Status == OfferStatus.Published),
            NeedsReview = offers.Count(o => o.Status == OfferStatus.NeedsReview),
            Deleted = offers.Count(o => o.Status == OfferStatus.Deleted)
        };
    }

    private bool IsRegexMatch(string pattern, string? input)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern))
            return false;

        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning("Regex timeout for pattern '{Pattern}'", pattern);
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid regex pattern '{Pattern}'", pattern);
            return false;
        }
    }

    private static (double value, string unit)? ParseAmountValue(string targetValue)
    {
        // Expected format: "value unit" e.g. "200 g", "1.5 kg"
        var parts = targetValue.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;

        if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return (value, parts[1].Trim());
        }

        return null;
    }
}
