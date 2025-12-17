using System.Text;
using System.Text.RegularExpressions;

namespace DealsScannerPro.Api.Services;

/// <summary>
/// Generates deterministic SKU keys from normalized offer data.
/// Format: {brand_norm}|{product_norm}|{variant_norm}|{container_type}|{net_amount_value}{net_amount_unit}
/// </summary>
public static class SkuKeyGenerator
{
    /// <summary>
    /// Generate a deterministic SKU key from offer fields.
    /// Returns null if required fields are missing.
    /// </summary>
    public static string? Generate(
        string? brandNorm,
        string? productNorm,
        string? variantNorm,
        string? containerType,
        double? netAmountValue,
        string? netAmountUnit)
    {
        // Product norm is required
        if (string.IsNullOrWhiteSpace(productNorm))
            return null;

        var parts = new List<string>
        {
            Normalize(brandNorm) ?? "null",
            Normalize(productNorm)!,
            Normalize(variantNorm) ?? "null",
            Normalize(containerType)?.ToLowerInvariant() ?? "null",
            FormatAmount(netAmountValue, netAmountUnit)
        };

        return string.Join("|", parts);
    }

    /// <summary>
    /// Normalize a string for use in SKU key:
    /// - Lowercase
    /// - Replace Danish characters (æ→ae, ø→oe, å→aa)
    /// - Remove special characters except hyphen
    /// - Trim whitespace
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var result = input.ToLowerInvariant().Trim();

        // Replace Danish characters
        result = result
            .Replace("æ", "ae")
            .Replace("ø", "oe")
            .Replace("å", "aa")
            .Replace("Æ", "ae")
            .Replace("Ø", "oe")
            .Replace("Å", "aa");

        // Remove special characters except hyphen and space
        result = Regex.Replace(result, @"[^a-z0-9\-\s]", "");

        // Replace spaces with hyphen
        result = Regex.Replace(result, @"\s+", "-");

        // Remove multiple consecutive hyphens
        result = Regex.Replace(result, @"-+", "-");

        // Trim hyphens from start/end
        result = result.Trim('-');

        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>
    /// Format amount as "{value}{unit}" for SKU key.
    /// Normalizes units: L→ml (x1000), kg→g (x1000)
    /// </summary>
    private static string FormatAmount(double? value, string? unit)
    {
        if (!value.HasValue || string.IsNullOrWhiteSpace(unit))
            return "null";

        var normalizedUnit = unit.ToLowerInvariant().Trim();
        var normalizedValue = value.Value;

        // Normalize to base units
        switch (normalizedUnit)
        {
            case "l":
            case "liter":
                normalizedValue *= 1000;
                normalizedUnit = "ml";
                break;
            case "kg":
            case "kilo":
                normalizedValue *= 1000;
                normalizedUnit = "g";
                break;
            case "cl":
                normalizedValue *= 10;
                normalizedUnit = "ml";
                break;
            case "dl":
                normalizedValue *= 100;
                normalizedUnit = "ml";
                break;
        }

        // Round to avoid floating point issues
        normalizedValue = Math.Round(normalizedValue, 0);

        return $"{normalizedValue:0}{normalizedUnit}";
    }
}
