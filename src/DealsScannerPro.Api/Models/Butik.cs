using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a store/supermarket.
/// PartitionKey: "butikker"
/// RowKey: {butik_id} (e.g., "netto", "rema")
/// </summary>
public class Butik : ITableEntity
{
    public string PartitionKey { get; set; } = "butikker";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Navn { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string PrimaerFarve { get; set; } = "#000000";
    public string SekundaerFarve { get; set; } = "#FFFFFF";
    public bool Aktiv { get; set; } = true;
}
