namespace Pos.Domain.Finance;

public static class CreditAccountCurrency
{
    public static decimal SumToUsd(IEnumerable<PaymentTender> tenders, decimal usdToVesRate)
    {
        ArgumentNullException.ThrowIfNull(tenders);
        return tenders.GroupBy(x => x.Currency)
            .Sum(group => ToUsd(group.Sum(x => x.Amount), group.Key, usdToVesRate));
    }

    public static decimal ToUsd(decimal amount, string currency, decimal usdToVesRate)
    {
        if (amount < 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        return currency switch
        {
            "USD" => decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            "VES" when usdToVesRate > 0m => decimal.Round(amount / usdToVesRate, 2, MidpointRounding.AwayFromZero),
            "VES" => throw new ArgumentOutOfRangeException(nameof(usdToVesRate)),
            _ => throw new ArgumentException("La moneda de origen debe ser USD o VES.", nameof(currency))
        };
    }

    public static decimal ToVes(decimal amountUsd, decimal usdToVesRate)
    {
        if (amountUsd < 0m) throw new ArgumentOutOfRangeException(nameof(amountUsd));
        if (usdToVesRate <= 0m) throw new ArgumentOutOfRangeException(nameof(usdToVesRate));
        return decimal.Round(amountUsd * usdToVesRate, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal UsdPerVes(decimal usdToVesRate)
    {
        if (usdToVesRate <= 0m) throw new ArgumentOutOfRangeException(nameof(usdToVesRate));
        return decimal.Round(1m / usdToVesRate, 8, MidpointRounding.AwayFromZero);
    }
}
