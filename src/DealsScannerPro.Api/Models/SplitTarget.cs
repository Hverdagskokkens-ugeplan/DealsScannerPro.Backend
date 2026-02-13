using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Deserialization target for SkuOverride.SplitIntoJson.
/// Each target defines a product to create from a split.
/// Fields that are null inherit from the parent offer.
/// </summary>
public class SplitTarget
{
    [JsonPropertyName("product_norm")]
    public string ProductNorm { get; set; } = string.Empty;

    [JsonPropertyName("variant_norm")]
    public string? VariantNorm { get; set; }

    [JsonPropertyName("brand_norm")]
    public string? BrandNorm { get; set; }

    [JsonPropertyName("net_amount_value")]
    public double? NetAmountValue { get; set; }

    [JsonPropertyName("net_amount_unit")]
    public string? NetAmountUnit { get; set; }

    [JsonPropertyName("container_type")]
    public string? ContainerType { get; set; }

    [JsonPropertyName("price_value")]
    public double? PriceValue { get; set; }

    [JsonPropertyName("sku_key")]
    public string? SkuKey { get; set; }
}
