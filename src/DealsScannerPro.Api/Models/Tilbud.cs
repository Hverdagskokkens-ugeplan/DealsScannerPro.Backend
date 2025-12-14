using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a deal/offer from a supermarket flyer.
/// PartitionKey: {butik}_{gyldig_fra}_{gyldig_til}
/// RowKey: {guid}
/// </summary>
public class Tilbud : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Deal properties
    public string Produkt { get; set; } = string.Empty;
    public double? TotalPris { get; set; }
    public double? PrisPerEnhed { get; set; }
    public string? Enhed { get; set; }
    public string? Maengde { get; set; }
    public double? MaengdeValue { get; set; }
    public string? MaengdeUnit { get; set; }
    public string Kategori { get; set; } = "Andet";
    public double? Konfidens { get; set; }
    public int Side { get; set; }
    public string KildeFil { get; set; } = string.Empty;
    public string? Varianter { get; set; } // JSON array as string
    public string? Kommentar { get; set; }

    // Metadata
    public string Butik { get; set; } = string.Empty;
    public DateTime GyldigFra { get; set; }
    public DateTime GyldigTil { get; set; }
    public DateTime Oprettet { get; set; } = DateTime.UtcNow;
}
