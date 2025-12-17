using Azure;
using Azure.Data.Tables;
using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a normalized offer from a supermarket flyer.
/// PartitionKey: {retailer}_{valid_from}_{valid_to}
/// RowKey: {sku_key} (deterministic for dedup)
/// </summary>
public class Offer : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Source
    public string Retailer { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }

    // Raw data
    public string ProductTextRaw { get; set; } = string.Empty;

    // Normalized fields (from GPT)
    public string? BrandNorm { get; set; }
    public string ProductNorm { get; set; } = string.Empty;
    public string? VariantNorm { get; set; }
    public string Category { get; set; } = "Andet";

    // Amount
    public double? NetAmountValue { get; set; }
    public string? NetAmountUnit { get; set; }  // ml, g, stk
    public int? PackCount { get; set; }
    public string? ContainerType { get; set; }  // CAN, BOTTLE, BAG, TRAY, BOX, JAR

    // Price
    public double? PriceValue { get; set; }
    public double? DepositValue { get; set; }  // Pant
    public double? PriceExclDeposit { get; set; }

    // Unit price (calculated)
    public double? UnitPriceValue { get; set; }
    public string? UnitPriceUnit { get; set; }  // kr/L, kr/kg, kr/stk

    // Identity
    public string? SkuKey { get; set; }

    // Comment
    public string? Comment { get; set; }

    // Confidence
    public double Confidence { get; set; }
    public string? ConfidenceDetailsJson { get; set; }  // JSON: {"price": 1.0, "amount": 0.9, ...}
    public string? ConfidenceReasonsJson { get; set; }  // JSON: ["Ingen pris fundet", ...]

    // Visual (for Review UI)
    public string? CropUrl { get; set; }  // Blob URL for bbox crop image

    // Trace (for debugging and review)
    public string? TraceJson { get; set; }  // JSON: {"page": 3, "bbox": [...], "text_lines": [...], "source_file": "..."}

    // Status
    public string Status { get; set; } = "needs_review";  // needs_review, published, deleted, low_confidence
    public string? ReviewReason { get; set; }

    // Scanner metadata
    public string? ScannerVersion { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
}

/// <summary>
/// Confidence details for an offer
/// </summary>
public class ConfidenceDetails
{
    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("brand")]
    public double Brand { get; set; }

    [JsonPropertyName("category")]
    public double Category { get; set; }

    [JsonPropertyName("retailer")]
    public double Retailer { get; set; }

    [JsonPropertyName("validity")]
    public double Validity { get; set; }
}

/// <summary>
/// Trace information for debugging and review
/// </summary>
public class OfferTrace
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("bbox")]
    public double[]? Bbox { get; set; }  // [x1, y1, x2, y2]

    [JsonPropertyName("text_lines")]
    public List<string>? TextLines { get; set; }

    [JsonPropertyName("source_file")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("crop_blob_url")]
    public string? CropBlobUrl { get; set; }
}

/// <summary>
/// Container types for products
/// </summary>
public static class ContainerTypes
{
    public const string Can = "CAN";
    public const string Bottle = "BOTTLE";
    public const string Bag = "BAG";
    public const string Tray = "TRAY";
    public const string Box = "BOX";
    public const string Jar = "JAR";
}

/// <summary>
/// Offer status values
/// </summary>
public static class OfferStatus
{
    public const string NeedsReview = "needs_review";
    public const string Published = "published";
    public const string Deleted = "deleted";
    public const string LowConfidence = "low_confidence";
}
