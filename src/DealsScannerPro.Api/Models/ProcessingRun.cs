using Azure;
using Azure.Data.Tables;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a processing run (upload or reprocess) for a flyer.
/// PartitionKey: flyer_id (e.g. "netto_2025-12-22_2025-12-28")
/// RowKey: Inverted timestamp for newest-first ordering
/// </summary>
public class ProcessingRun : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string RunId { get; set; } = string.Empty;
    public string FlyerId { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public int RunNumber { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string Status { get; set; } = "running"; // running, completed, failed
    public string TriggerType { get; set; } = string.Empty; // upload, reprocess, manual
    public string TriggeredBy { get; set; } = string.Empty;

    public string? RulesVersionHash { get; set; }
    public string? MetricsJson { get; set; }

    public int OffersCreated { get; set; }
    public int OffersUpdated { get; set; }

    public string? ErrorsJson { get; set; }

    /// <summary>
    /// Generate a RowKey that sorts newest first.
    /// Uses inverted ticks so newest entries have lowest sort value.
    /// </summary>
    public static string GenerateRowKey(DateTime timestamp)
    {
        var invertedTicks = DateTime.MaxValue.Ticks - timestamp.Ticks;
        return invertedTicks.ToString("D19");
    }

    public RunMetrics? GetMetrics()
    {
        if (string.IsNullOrEmpty(MetricsJson)) return null;
        return JsonSerializer.Deserialize<RunMetrics>(MetricsJson);
    }

    public void SetMetrics(RunMetrics metrics)
    {
        MetricsJson = JsonSerializer.Serialize(metrics);
    }

    public List<string>? GetErrors()
    {
        if (string.IsNullOrEmpty(ErrorsJson)) return null;
        return JsonSerializer.Deserialize<List<string>>(ErrorsJson);
    }

    public void SetErrors(List<string> errors)
    {
        ErrorsJson = JsonSerializer.Serialize(errors);
    }
}

public class RunMetrics
{
    [JsonPropertyName("total_offers")]
    public int TotalOffers { get; set; }

    [JsonPropertyName("high_confidence")]
    public int HighConfidence { get; set; }

    [JsonPropertyName("medium_confidence")]
    public int MediumConfidence { get; set; }

    [JsonPropertyName("low_confidence")]
    public int LowConfidence { get; set; }

    [JsonPropertyName("avg_confidence")]
    public double AvgConfidence { get; set; }

    [JsonPropertyName("by_category")]
    public Dictionary<string, int> ByCategory { get; set; } = new();
}
