using Azure;
using Azure.Data.Tables;
using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a scan run log entry for diagnostics.
/// PartitionKey: "scanlogs" (single partition for easy querying)
/// RowKey: Inverted timestamp for newest-first ordering
/// </summary>
public class ScanLog : ITableEntity
{
    public string PartitionKey { get; set; } = "scanlogs";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Scan identification
    public string ScanId { get; set; } = string.Empty;
    public DateTime ScanTimestamp { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }

    // Services used (JSON serialized)
    public string? ServicesUsedJson { get; set; }

    // Results
    public int PagesScanned { get; set; }
    public int OffersDetected { get; set; }
    public int OffersExtracted { get; set; }
    public int OffersWithCandidates { get; set; }
    public double AvgConfidence { get; set; }
    public int OffersUploaded { get; set; }

    // Status
    public string Status { get; set; } = "completed";  // completed, failed, partial
    public string? ErrorMessage { get; set; }

    // Warnings (JSON array)
    public string? WarningsJson { get; set; }

    /// <summary>
    /// Generate a RowKey that sorts newest first.
    /// Uses inverted ticks so newest entries have lowest sort value.
    /// </summary>
    public static string GenerateRowKey(DateTime timestamp)
    {
        var invertedTicks = DateTime.MaxValue.Ticks - timestamp.Ticks;
        return invertedTicks.ToString("D19");
    }
}

/// <summary>
/// Services used during a scan.
/// </summary>
public class ScanServicesUsed
{
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "unknown";  // document_intelligence, pymupdf_fallback

    [JsonPropertyName("normalization")]
    public string Normalization { get; set; } = "unknown";  // openai_gpt4, rule_based_fallback

    [JsonPropertyName("cropping")]
    public string Cropping { get; set; } = "disabled";  // enabled, disabled
}

/// <summary>
/// Request model for logging a scan.
/// </summary>
public class LogScanRequest
{
    [JsonPropertyName("scan_id")]
    public string? ScanId { get; set; }

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("retailer")]
    public string Retailer { get; set; } = string.Empty;

    [JsonPropertyName("valid_from")]
    public string? ValidFrom { get; set; }

    [JsonPropertyName("valid_to")]
    public string? ValidTo { get; set; }

    [JsonPropertyName("services_used")]
    public ScanServicesUsed? ServicesUsed { get; set; }

    [JsonPropertyName("pages_scanned")]
    public int PagesScanned { get; set; }

    [JsonPropertyName("offers_detected")]
    public int OffersDetected { get; set; }

    [JsonPropertyName("offers_extracted")]
    public int OffersExtracted { get; set; }

    [JsonPropertyName("offers_with_candidates")]
    public int OffersWithCandidates { get; set; }

    [JsonPropertyName("avg_confidence")]
    public double AvgConfidence { get; set; }

    [JsonPropertyName("offers_uploaded")]
    public int OffersUploaded { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "completed";

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("warnings")]
    public List<string>? Warnings { get; set; }
}
