using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Retailer-specific scanning rule learned from human review.
/// PartitionKey: {retailer} (lowercase)
/// RowKey: {rule_type}_{guid}
/// </summary>
public class RetailerRule : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;  // retailer
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Rule definition
    public string RuleType { get; set; } = string.Empty;      // e.g. price_pattern, skip_pattern
    public string Pattern { get; set; } = string.Empty;        // regex or text pattern
    public string Action { get; set; } = string.Empty;         // what to do when pattern matches
    public string? TargetValue { get; set; }                   // replacement/mapped value

    // Provenance
    public string? CreatedFrom { get; set; }                   // e.g. "review:abc123" linking to review
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Usage tracking
    public int HitCount { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    // Human-readable description
    public string? Description { get; set; }
}

/// <summary>
/// Rule types for retailer scanning rules.
/// </summary>
public static class RuleTypes
{
    /// <summary>
    /// Pattern for detecting/correcting prices (e.g. "XX.-" format).
    /// </summary>
    public const string PricePattern = "price_pattern";

    /// <summary>
    /// Pattern for text that should be skipped (non-product content).
    /// </summary>
    public const string SkipPattern = "skip_pattern";

    /// <summary>
    /// Pattern for detecting/normalizing product amounts (e.g. "200 g", "1 kg").
    /// </summary>
    public const string AmountPattern = "amount_pattern";

    /// <summary>
    /// Mapping from product text to category.
    /// </summary>
    public const string CategoryMap = "category_map";

    public static readonly string[] All = { PricePattern, SkipPattern, AmountPattern, CategoryMap };
}
