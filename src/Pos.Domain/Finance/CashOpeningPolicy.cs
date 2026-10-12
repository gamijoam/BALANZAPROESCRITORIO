using Pos.Domain;

namespace Pos.Domain.Finance;

public sealed record CashOpeningAmounts(string Currency, decimal OpeningUsd, decimal OpeningVes);

public static class CashOpeningPolicy
{
    public static CashOpeningAmounts Resolve(decimal? legacyOpening, string? legacyCurrency,
        decimal? openingUsd, decimal? openingVes)
    {
        var sourceCurrency = string.IsNullOrWhiteSpace(legacyCurrency)
            ? "VES"
            : legacyCurrency.Trim().ToUpperInvariant();
        if (sourceCurrency is not ("VES" or "USD"))
            throw new DomainRuleException("cash_currency_invalid");

        var usd = openingUsd ?? (sourceCurrency == "USD" ? legacyOpening ?? 0m : 0m);
        var ves = openingVes ?? (sourceCurrency == "VES" ? legacyOpening ?? 0m : 0m);
        if (usd < 0m || ves < 0m)
            throw new DomainRuleException("cash_opening_amount_invalid");

        return new CashOpeningAmounts("VES", usd, ves);
    }
}
