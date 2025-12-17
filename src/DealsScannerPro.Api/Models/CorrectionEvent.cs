using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Audit trail for corrections made during review.
/// PartitionKey: {offer_id}
/// RowKey: {timestamp}_{event_type}
/// </summary>
public class CorrectionEvent : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;  // offer_id
    public string RowKey { get; set; } = string.Empty;        // timestamp_eventtype
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Event info
    public string EventType { get; set; } = string.Empty;  // FIELD_CORRECTION, STATUS_CHANGE, SKU_OVERRIDE_CREATED
    public string OfferId { get; set; } = string.Empty;

    // For field corrections
    public string? Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    // For status changes
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }

    // Metadata
    public string? Reason { get; set; }
    public string CorrectedBy { get; set; } = string.Empty;
    public DateTime CorrectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Correction event types
/// </summary>
public static class CorrectionEventTypes
{
    public const string FieldCorrection = "FIELD_CORRECTION";
    public const string StatusChange = "STATUS_CHANGE";
    public const string SkuOverrideCreated = "SKU_OVERRIDE_CREATED";
    public const string BatchApproved = "BATCH_APPROVED";
    public const string Deleted = "DELETED";
}
