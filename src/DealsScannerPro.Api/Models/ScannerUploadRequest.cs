using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Request model for uploading scanned offers from the new scanner v2.0.
/// </summary>
public class ScannerUploadRequest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0";

    [JsonPropertyName("meta")]
    public ScannerMeta Meta { get; set; } = new();

    [JsonPropertyName("scan_stats")]
    public ScanStats? ScanStats { get; set; }

    [JsonPropertyName("offers")]
    public List<ScannedOffer> Offers { get; set; } = new();
}

public class ScanStats
{
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("total_blocks")]
    public int TotalBlocks { get; set; }

    [JsonPropertyName("offers_detected")]
    public int OffersDetected { get; set; }

    [JsonPropertyName("offers_extracted")]
    public int OffersExtracted { get; set; }

    [JsonPropertyName("scanner_version")]
    public string ScannerVersion { get; set; } = "2.0.0";
}

public class ScannerMeta
{
    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("retailer")]
    public string? Retailer { get; set; }

    [JsonPropertyName("retailer_confidence")]
    public double? RetailerConfidence { get; set; }

    [JsonPropertyName("valid_from")]
    public string? ValidFrom { get; set; }

    [JsonPropertyName("valid_to")]
    public string? ValidTo { get; set; }

    [JsonPropertyName("validity_confidence")]
    public double? ValidityConfidence { get; set; }

    [JsonPropertyName("scanned_at")]
    public string? ScannedAt { get; set; }

    [JsonPropertyName("scanner_version")]
    public string? ScannerVersion { get; set; }

    [JsonPropertyName("total_pages")]
    public int? TotalPages { get; set; }
}

public class ScannedOffer
{
    // Raw data
    [JsonPropertyName("product_text_raw")]
    public string ProductTextRaw { get; set; } = string.Empty;

    // Normalized fields
    [JsonPropertyName("brand_norm")]
    public string? BrandNorm { get; set; }

    [JsonPropertyName("product_norm")]
    public string ProductNorm { get; set; } = string.Empty;

    [JsonPropertyName("variant_norm")]
    public string? VariantNorm { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Andet";

    // Amount
    [JsonPropertyName("net_amount_value")]
    public double? NetAmountValue { get; set; }

    [JsonPropertyName("net_amount_unit")]
    public string? NetAmountUnit { get; set; }

    [JsonPropertyName("pack_count")]
    public int? PackCount { get; set; }

    [JsonPropertyName("container_type")]
    public string? ContainerType { get; set; }

    // Price
    [JsonPropertyName("price_value")]
    public double? PriceValue { get; set; }

    [JsonPropertyName("deposit_value")]
    public double? DepositValue { get; set; }

    [JsonPropertyName("price_excl_deposit")]
    public double? PriceExclDeposit { get; set; }

    [JsonPropertyName("unit_price_value")]
    public double? UnitPriceValue { get; set; }

    [JsonPropertyName("unit_price_unit")]
    public string? UnitPriceUnit { get; set; }

    // Identity
    [JsonPropertyName("sku_key")]
    public string? SkuKey { get; set; }

    // Comment
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    // Confidence
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("confidence_details")]
    public Dictionary<string, double>? ConfidenceDetails { get; set; }

    [JsonPropertyName("confidence_reasons")]
    public List<string>? ConfidenceReasons { get; set; }

    // Status (based on confidence)
    [JsonPropertyName("status")]
    public string Status { get; set; } = "needs_review";

    // Visual (for Review UI)
    [JsonPropertyName("crop_url")]
    public string? CropUrl { get; set; }

    // Trace
    [JsonPropertyName("trace")]
    public ScannedOfferTrace? Trace { get; set; }

    // Learning Mode - Candidates
    [JsonPropertyName("candidates")]
    public ScannedOfferCandidates? Candidates { get; set; }
}

public class ScannedOfferTrace
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("bbox")]
    public double[]? Bbox { get; set; }

    [JsonPropertyName("text_lines")]
    public List<string>? TextLines { get; set; }

    [JsonPropertyName("source_file")]
    public string? SourceFile { get; set; }
}

public class ScannedOfferCandidates
{
    [JsonPropertyName("price_candidates")]
    public List<ScannedPriceCandidate>? PriceCandidates { get; set; }

    [JsonPropertyName("amount_candidates")]
    public List<ScannedAmountCandidate>? AmountCandidates { get; set; }

    [JsonPropertyName("selected")]
    public ScannedSelectedCandidates? Selected { get; set; }
}

public class ScannedPriceCandidate
{
    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public class ScannedAmountCandidate
{
    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class ScannedSelectedCandidates
{
    [JsonPropertyName("price_index")]
    public int? PriceIndex { get; set; }

    [JsonPropertyName("amount_index")]
    public int? AmountIndex { get; set; }
}
