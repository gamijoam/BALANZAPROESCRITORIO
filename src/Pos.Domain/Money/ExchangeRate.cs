namespace Pos.Domain.Finance;

/// <summary>An immutable, attributable conversion rate from one currency to another.</summary>
public sealed record ExchangeRate
{
    public Currency FromCurrency { get; }
    public Currency ToCurrency { get; }
    public decimal Rate { get; }
    public DateTimeOffset EffectiveAtUtc { get; }
    public string Source { get; }

    private ExchangeRate(Currency fromCurrency, Currency toCurrency, decimal rate, DateTimeOffset effectiveAtUtc, string source)
    {
        FromCurrency = fromCurrency;
        ToCurrency = toCurrency;
        Rate = rate;
        EffectiveAtUtc = effectiveAtUtc;
        Source = source;
    }

    public static ExchangeRate Create(
        Currency fromCurrency,
        Currency toCurrency,
        decimal rate,
        DateTimeOffset effectiveAtUtc,
        string source)
    {
        if (!Enum.IsDefined(fromCurrency)) throw new ArgumentOutOfRangeException(nameof(fromCurrency));
        if (!Enum.IsDefined(toCurrency)) throw new ArgumentOutOfRangeException(nameof(toCurrency));
        if (fromCurrency == toCurrency) throw new ArgumentException("Currencies must be different.");
        if (rate <= 0m) throw new ArgumentOutOfRangeException(nameof(rate));
        if (effectiveAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The effective time must be UTC.", nameof(effectiveAtUtc));
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A rate source is required.", nameof(source));

        return new ExchangeRate(fromCurrency, toCurrency, rate, effectiveAtUtc, source.Trim());
    }

    public Money Convert(Money amount, int targetScale, MidpointRounding mode = MidpointRounding.AwayFromZero)
    {
        if (amount.Currency != FromCurrency)
            throw new DomainRuleException("The amount currency does not match the exchange rate source currency.");

        return new Money(amount.Amount * Rate, ToCurrency).Round(targetScale, mode);
    }
}
