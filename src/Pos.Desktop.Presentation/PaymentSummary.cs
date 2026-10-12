namespace Pos.Desktop.Presentation;

public sealed record PaymentSummary(decimal Paid, decimal Remaining, decimal RoundingAdjustment, bool CanConfirm, bool CanAddPayment, string Guidance,
    decimal? RemainingOtherCurrencyEquivalent)
{
    public static PaymentSummary Calculate(decimal total, string saleCurrency, IReadOnlyCollection<PaymentRow> rows, decimal? usdToVes)
    {
        var validRows = rows.Where(x => x.TryGetAmount(out var amount) && amount > 0m).ToArray();
        var hasIncompleteRows = validRows.Length != rows.Count;
        if (validRows.Length == 0)
        {
            var incompleteGuidance = rows.Count == 0
                ? "Selecciona un método de pago para continuar."
                : "Completa o elimina el pago sin monto.";
            return NotSettled(total, saleCurrency, usdToVes, incompleteGuidance, canAddPayment: rows.Count == 0);
        }
        if (validRows.Any(x => x.Currency != saleCurrency) && usdToVes is null or <= 0m)
            return NotSettled(total, saleCurrency, usdToVes, "Registra una tasa vigente para convertir pagos en otra moneda.");

        Pos.Domain.Finance.PaymentSettlementResult settlement;
        try
        {
            settlement = Pos.Domain.Finance.PaymentSettlementPolicy.Evaluate(total, saleCurrency,
                validRows.Select(x => new Pos.Domain.Finance.PaymentTender(Parse(x), x.Currency)), usdToVes);
        }
        catch (ArgumentException)
        {
            return NotSettled(total, saleCurrency, usdToVes, "Los pagos deben estar expresados en centavos de USD o VES.");
        }

        var remaining = settlement.IsSettled ? 0m : settlement.RoundingAdjustment;
        var guidance = hasIncompleteRows
            ? remaining > 0m
                ? "Completa o elimina los pagos sin monto antes de continuar."
                : "Completa o elimina los pagos sin monto antes de confirmar."
            : settlement.IsSettled && settlement.RoundingAdjustment != 0m
            ? $"Diferencia de conversión {saleCurrency} {PosNumberFormat.FormatAmount(decimal.Abs(settlement.RoundingAdjustment))}; se registrará como ajuste de redondeo."
            : remaining > 0m
                ? "Agrega otro pago o ajusta los importes."
                : remaining < 0m
                    ? "Los pagos superan el total permitido. Reduce o ajusta un pago."
                    : string.Empty;

        return new PaymentSummary(settlement.PaidInSaleCurrency, remaining,
            settlement.IsSettled ? settlement.RoundingAdjustment : 0m,
            settlement.IsSettled && !hasIncompleteRows,
            !hasIncompleteRows && !settlement.IsSettled && remaining > settlement.AllowedDifference, guidance,
            ToOtherCurrencyEquivalent(remaining, saleCurrency, usdToVes));

        static decimal Parse(PaymentRow row) => row.TryGetAmount(out var amount) ? amount : 0m;
    }

    private static PaymentSummary NotSettled(decimal total, string saleCurrency, decimal? usdToVes, string guidance, bool canAddPayment = true) =>
        new(0m, total, 0m, false, canAddPayment, guidance,
            ToOtherCurrencyEquivalent(total, saleCurrency, usdToVes));

    private static decimal? ToOtherCurrencyEquivalent(decimal amount, string currency, decimal? usdToVes) => currency switch
    {
        "USD" when usdToVes is > 0m => decimal.Round(amount * usdToVes.Value, 2, MidpointRounding.AwayFromZero),
        "VES" when usdToVes is > 0m => decimal.Round(amount / usdToVes.Value, 2, MidpointRounding.AwayFromZero),
        _ => null
    };

}
