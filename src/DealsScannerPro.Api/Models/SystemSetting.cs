using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a system configuration setting.
/// PartitionKey: "settings" (fixed — enables full-table cache load)
/// RowKey: {setting_key} (e.g., "archive_retention_days")
/// </summary>
public class SystemSetting : ITableEntity
{
    public string PartitionKey { get; set; } = "settings";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DataType { get; set; }  // "int", "bool", "string"
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Well-known setting keys
/// </summary>
public static class SettingKeys
{
    public const string ArchiveRetentionDays = "archive_retention_days";
    public const string ArchiveEnabled = "archive_enabled";
    public const string ArchiveGraceDays = "archive_grace_days";
}
