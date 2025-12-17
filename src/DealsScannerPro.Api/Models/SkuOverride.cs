using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// SKU override for matching/splitting products.
/// PartitionKey: {retailer}
/// RowKey: {override_type}_{hash of sku_a + sku_b}
/// </summary>
public class SkuOverride : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;  // retailer
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Override type
    public string OverrideType { get; set; } = string.Empty;  // MATCH or SPLIT

    // For MATCH: two SKUs that should be treated as the same
    public string SkuA { get; set; } = string.Empty;
    public string? SkuB { get; set; }

    // For SPLIT: one SKU that should be split into multiple
    public string? SplitIntoJson { get; set; }  // JSON array of SKU keys

    // Metadata
    public string Retailer { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Override types
/// </summary>
public static class OverrideTypes
{
    /// <summary>
    /// Two different SKU keys should be treated as the same product
    /// Example: "coca-cola|cola|original" and "coca-cola|cola|classic" are the same
    /// </summary>
    public const string Match = "MATCH";

    /// <summary>
    /// One SKU key should be split into multiple distinct products
    /// Example: "arla|maelk|null" should be split into "arla|soedmaelk|null" and "arla|letmaelk|null"
    /// </summary>
    public const string Split = "SPLIT";
}
