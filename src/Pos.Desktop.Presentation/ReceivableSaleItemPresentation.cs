namespace Pos.Desktop.Presentation;

public static class ReceivableSaleItemPresentation
{
    public static string Format(IReadOnlyList<ReceivableSaleItemInfo> items)
    {
        if (items.Count == 0) return "—";

        return string.Join(Environment.NewLine, items.Select(item =>
        {
            var name = string.IsNullOrWhiteSpace(item.Description) ? item.Sku : item.Description;
            var amount = item.AmountUsd is decimal usd
                ? $"USD {PosNumberFormat.FormatAmount(usd)}"
                : "USD no disponible (sin tasa histórica)";
            return $"{name} × {PosNumberFormat.FormatQuantity(item.Quantity)} · {amount}";
        }));
    }
}
