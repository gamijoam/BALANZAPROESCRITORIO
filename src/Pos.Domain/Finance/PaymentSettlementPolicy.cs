namespace Pos.Domain.Finance;

public sealed record PaymentTender(decimal Amount, string Currency);

/// <param name="PaidInSaleCurrency">Tender total converted to the sale currency and rounded to its minor unit.</param>
/// <param name="RoundingAdjustment">Sale total minus converted tender. A negative value means tender exceeded the sale.</param>
/// <param name="AllowedDifference">Maximum conversion difference from half a minor unit of the foreign tender currency.</param>
public sealed record PaymentSettlementResult(decimal PaidInSaleCurrency, decimal RoundingAdjustment,
    decimal AllowedDifference, bool IsSettled);

public static class PaymentSettlementPolicy
{
    private const decimal HalfCent = 0.005m;

    public static PaymentSettlementResult Evaluate(decimal saleTotal, string saleCurrency,
        IEnumerable<PaymentTender> payments, decimal? usdToVes)
    {
        ArgumentNullException.ThrowIfNull(payments);
        if (saleTotal <= 0m || saleCurrency is not ("USD" or "VES"))
            throw new ArgumentOutOfRangeException(nameof(saleTotal), "La venta debe tener un total positivo en USD o VES.");

        var tenders = payments.ToArray();
        if (tenders.Length == 0 || tenders.Any(x => x.Amount <= 0m || x.Currency is not ("USD" or "VES") || decimal.Round(x.Amount, 2) != x.Amount))
            throw new ArgumentException("Cada pago debe ser positivo y estar expresado en centavos de USD o VES.", nameof(payments));

        var paid = 0m;
        var allowedDifference = 0m;
        foreach (var group in tenders.GroupBy(x => x.Currency))
        {
            var amount = group.Sum(x => x.Amount);
            if (group.Key == saleCurrency)
            {
                paid += amount;
                continue;
            }

            if (usdToVes is not > 0m)
                throw new ArgumentException("Se requiere una tasa vigente para convertir pagos en otra moneda.", nameof(usdToVes));

            var rate = group.Key == "USD" ? usdToVes.Value : 1m / usdToVes.Value;
            paid += decimal.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
            allowedDifference += HalfCent * rate;
        }

        paid = decimal.Round(paid, 2, MidpointRounding.AwayFromZero);
        var adjustment = decimal.Round(saleTotal - paid, 2, MidpointRounding.AwayFromZero);
        return new PaymentSettlementResult(paid, adjustment, allowedDifference,
            decimal.Abs(adjustment) <= allowedDifference);
    }
}
