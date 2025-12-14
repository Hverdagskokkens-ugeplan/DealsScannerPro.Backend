using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Request model for uploading scanned deals.
/// Matches the JSON output from DealsScannerPro Python scanner.
/// </summary>
public class UploadRequest
{
    [JsonPropertyName("meta")]
    public UploadMeta Meta { get; set; } = new();

    [JsonPropertyName("statistik")]
    public UploadStatistik? Statistik { get; set; }

    [JsonPropertyName("tilbud")]
    public List<UploadTilbud> Tilbud { get; set; } = new();
}

public class UploadMeta
{
    [JsonPropertyName("kilde_fil")]
    public string KildeFil { get; set; } = string.Empty;

    [JsonPropertyName("butik")]
    public string Butik { get; set; } = string.Empty;

    [JsonPropertyName("gyldig_fra")]
    public string? GyldigFra { get; set; }

    [JsonPropertyName("gyldig_til")]
    public string? GyldigTil { get; set; }

    [JsonPropertyName("uge")]
    public int? Uge { get; set; }

    [JsonPropertyName("scannet_tidspunkt")]
    public string? ScannetTidspunkt { get; set; }

    [JsonPropertyName("scanner_version")]
    public string? ScannerVersion { get; set; }
}

public class UploadStatistik
{
    [JsonPropertyName("antal_tilbud")]
    public int AntalTilbud { get; set; }

    [JsonPropertyName("høj_konfidens")]
    public int HoejKonfidens { get; set; }

    [JsonPropertyName("skal_gennemses")]
    public int SkalGennemses { get; set; }
}

public class UploadTilbud
{
    [JsonPropertyName("produkt")]
    public string Produkt { get; set; } = string.Empty;

    [JsonPropertyName("total_pris")]
    public double TotalPris { get; set; }

    [JsonPropertyName("pris_per_enhed")]
    public double? PrisPerEnhed { get; set; }

    [JsonPropertyName("enhed")]
    public string? Enhed { get; set; }

    [JsonPropertyName("maengde")]
    public string? Maengde { get; set; }

    [JsonPropertyName("maengde_normaliseret")]
    public MaengdeNormaliseret? MaengdeNormaliseret { get; set; }

    [JsonPropertyName("kategori")]
    public string Kategori { get; set; } = "Andet";

    [JsonPropertyName("konfidens")]
    public double Konfidens { get; set; }

    [JsonPropertyName("needs_review")]
    public bool NeedsReview { get; set; }

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("varianter")]
    public List<string>? Varianter { get; set; }

    [JsonPropertyName("kommentar")]
    public string? Kommentar { get; set; }
}

public class MaengdeNormaliseret
{
    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;
}
