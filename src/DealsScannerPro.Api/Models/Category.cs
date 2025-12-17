using Azure;
using Azure.Data.Tables;

namespace DealsScannerPro.Api.Models;

/// <summary>
/// Represents a product category for classification.
/// PartitionKey: "categories"
/// RowKey: {category_id} (e.g., "mejeri", "koed", "drikkevarer")
/// </summary>
public class Category : ITableEntity
{
    public string PartitionKey { get; set; } = "categories";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>
    /// Display name for the category (e.g., "Mejeri", "Kød", "Drikkevarer")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated keywords for matching products to this category.
    /// Example: "mælk,ost,yoghurt,smør,fløde,skyr"
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what belongs in this category.
    /// Used in GPT prompts for better classification.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Sort order for display (lower = first)
    /// </summary>
    public int SortOrder { get; set; } = 100;

    /// <summary>
    /// Whether this category is active
    /// </summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// Parent category ID for hierarchical categorization (optional)
    /// </summary>
    public string? ParentCategoryId { get; set; }

    /// <summary>
    /// Icon name or emoji for UI display
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Get keywords as a list
    /// </summary>
    public List<string> GetKeywordList()
    {
        if (string.IsNullOrWhiteSpace(Keywords))
            return new List<string>();

        return Keywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant())
            .ToList();
    }
}
