using Azure;
using Azure.Data.Tables;
using System.Text.Json.Serialization;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Canonical product alias group for cross-store matching.
/// PartitionKey: "aliases" (fixed — enables full-table cache load)
/// RowKey: {canonical_id} (deterministic from normalized brand+product+variant)
/// </summary>
public class ProductAlias : ITableEntity
{
    public string PartitionKey { get; set; } = "aliases";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string CanonicalBrand { get; set; } = string.Empty;
    public string CanonicalProduct { get; set; } = string.Empty;
    public string? CanonicalVariant { get; set; }

    /// <summary>Comma-separated searchable terms from all members</summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>Denormalized JSON array of ProductAliasMemberDto</summary>
    public string? MembersJson { get; set; }

    public int MemberCount { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public int MatchCount { get; set; }
}

/// <summary>
/// Individual SKU key membership in an alias group (reverse lookup table).
/// PartitionKey: {retailer} (enables per-retailer batch resolve)
/// RowKey: {sku_key_hash} (SHA256[0..32] — sku_key contains | which causes URL issues)
/// </summary>
public class ProductAliasMember : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;  // retailer
    public string RowKey { get; set; } = string.Empty;        // sku_key_hash
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string AliasId { get; set; } = string.Empty;
    public string SkuKey { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public string? BrandNorm { get; set; }
    public string? ProductNorm { get; set; }
    public string? VariantNorm { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string AddedBy { get; set; } = string.Empty;
}

/// <summary>
/// DTO for serializing members into ProductAlias.MembersJson
/// </summary>
public class ProductAliasMemberDto
{
    [JsonPropertyName("sku_key")]
    public string SkuKey { get; set; } = string.Empty;

    [JsonPropertyName("retailer")]
    public string Retailer { get; set; } = string.Empty;

    [JsonPropertyName("brand_norm")]
    public string? BrandNorm { get; set; }

    [JsonPropertyName("product_norm")]
    public string? ProductNorm { get; set; }

    [JsonPropertyName("variant_norm")]
    public string? VariantNorm { get; set; }
}
