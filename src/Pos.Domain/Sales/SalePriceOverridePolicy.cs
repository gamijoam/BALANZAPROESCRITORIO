namespace Pos.Domain.Sales;

/// <summary>Validates a one-sale price override before a sale is priced or persisted.</summary>
public static class SalePriceOverridePolicy
{
    public static bool IsValid(decimal? unitPrice, string? reason)
    {
        if (unitPrice is null) return string.IsNullOrWhiteSpace(reason);
        return unitPrice > 0m && decimal.Round(unitPrice.Value, 4) == unitPrice.Value &&
               reason?.Trim().Length is >= 3 and <= 250;
    }
}
