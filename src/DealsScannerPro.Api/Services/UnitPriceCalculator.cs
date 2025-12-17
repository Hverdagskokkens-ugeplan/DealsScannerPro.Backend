namespace DealsScannerPro.Api.Services;

/// <summary>
/// Calculates unit prices (kr/L, kr/kg, kr/stk) from offer data.
/// All calculations are deterministic.
/// </summary>
public static class UnitPriceCalculator
{
    /// <summary>
    /// Calculate unit price from offer data.
    /// Returns (unitPriceValue, unitPriceUnit) or (null, null) if cannot calculate.
    /// </summary>
    public static (double? Value, string? Unit) Calculate(
        double? priceValue,
        double? depositValue,
        double? netAmountValue,
        string? netAmountUnit,
        int? packCount)
    {
        if (!priceValue.HasValue || priceValue.Value <= 0)
            return (null, null);

        if (!netAmountValue.HasValue || netAmountValue.Value <= 0)
            return (null, null);

        if (string.IsNullOrWhiteSpace(netAmountUnit))
            return (null, null);

        // Price excluding deposit
        var effectivePrice = priceValue.Value - (depositValue ?? 0);
        if (effectivePrice <= 0)
            effectivePrice = priceValue.Value;

        // Total amount (considering pack count)
        var totalAmount = netAmountValue.Value * (packCount ?? 1);

        var unit = netAmountUnit.ToLowerInvariant().Trim();

        // Calculate based on unit type
        return unit switch
        {
            // Volume -> kr/L
            "ml" => (Math.Round(effectivePrice / (totalAmount / 1000), 2), "kr/L"),
            "cl" => (Math.Round(effectivePrice / (totalAmount / 100), 2), "kr/L"),
            "dl" => (Math.Round(effectivePrice / (totalAmount / 10), 2), "kr/L"),
            "l" or "liter" => (Math.Round(effectivePrice / totalAmount, 2), "kr/L"),

            // Weight -> kr/kg
            "g" or "gram" => (Math.Round(effectivePrice / (totalAmount / 1000), 2), "kr/kg"),
            "kg" or "kilo" => (Math.Round(effectivePrice / totalAmount, 2), "kr/kg"),

            // Count -> kr/stk
            "stk" or "stk." or "pk" or "pak" => (Math.Round(effectivePrice / totalAmount, 2), "kr/stk"),

            // Unknown unit - cannot calculate
            _ => (null, null)
        };
    }

    /// <summary>
    /// Calculate price excluding deposit.
    /// </summary>
    public static double? CalculatePriceExclDeposit(double? priceValue, double? depositValue)
    {
        if (!priceValue.HasValue)
            return null;

        if (!depositValue.HasValue || depositValue.Value <= 0)
            return priceValue.Value;

        var result = priceValue.Value - depositValue.Value;
        return result > 0 ? Math.Round(result, 2) : priceValue.Value;
    }
}
