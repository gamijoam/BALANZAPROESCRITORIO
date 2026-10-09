namespace Pos.Domain.Finance;

/// <summary>A signed monetary value. Rounding is always explicit at a business boundary.</summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public Currency Currency { get; }

    public Money(decimal amount, Currency currency)
    {
        if (!Enum.IsDefined(currency))
            throw new ArgumentOutOfRangeException(nameof(currency));

        Amount = amount;
        Currency = currency;
    }

    public Money Round(int scale, MidpointRounding mode = MidpointRounding.AwayFromZero)
    {
        if (scale is < 0 or > 28)
            throw new ArgumentOutOfRangeException(nameof(scale));

        return new Money(decimal.Round(Amount, scale, mode), Currency);
    }

    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new DomainRuleException("Money values must use the same currency.");
    }
}
