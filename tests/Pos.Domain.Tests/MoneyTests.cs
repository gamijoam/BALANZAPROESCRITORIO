using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Money_supports_signed_arithmetic_without_implicit_rounding()
    {
        var amount = new Money(1.239m, Currency.USD);

        Assert.Equal(new Money(3.239m, Currency.USD), amount + new Money(2m, Currency.USD));
        Assert.Equal(new Money(-0.761m, Currency.USD), amount - new Money(2m, Currency.USD));
        Assert.Equal(new Money(2.478m, Currency.USD), amount.Multiply(2m));
    }

    [Fact]
    public void Addition_requires_matching_currencies()
    {
        var usd = new Money(10m, Currency.USD);
        var ves = new Money(10m, Currency.VES);

        Assert.Throws<DomainRuleException>(() => _ = usd + ves);
    }

    [Fact]
    public void Rounding_uses_the_explicit_scale_and_midpoint_rule()
    {
        var rounded = new Money(1.005m, Currency.USD).Round(2);

        Assert.Equal(1.01m, rounded.Amount);
    }

    [Fact]
    public void Money_rounding_rejects_scales_outside_decimal_precision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(1m, Currency.USD).Round(29));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(1m, Currency.USD).Round(-1));
    }

    [Fact]
    public void Money_rounding_accepts_both_supported_scale_boundaries()
    {
        Assert.Equal(new Money(2m, Currency.USD), new Money(2m, Currency.USD).Round(0));
        Assert.Equal(new Money(2m, Currency.USD), new Money(2m, Currency.USD).Round(28));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(1m, (Currency)999));
    }

    [Fact]
    public void Exchange_rate_converts_and_preserves_the_target_currency()
    {
        var rate = ExchangeRate.Create(
            Currency.USD,
            Currency.VES,
            40m,
            new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero),
            "manual");

        var converted = rate.Convert(new Money(2.5m, Currency.USD), 2);

        Assert.Equal(new Money(100m, Currency.VES), converted);
    }

    [Fact]
    public void Exchange_rate_rejects_non_positive_values_and_non_utc_times()
    {
        var localTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(-4));

        Assert.Throws<ArgumentOutOfRangeException>(() => ExchangeRate.Create(
            Currency.USD, Currency.VES, 0m, DateTimeOffset.UtcNow, "manual"));
        Assert.Throws<ArgumentException>(() => ExchangeRate.Create(
            Currency.USD, Currency.VES, 40m, localTime, "manual"));
    }

    [Fact]
    public void Exchange_rate_rejects_an_amount_in_the_wrong_source_currency()
    {
        var rate = ExchangeRate.Create(Currency.USD, Currency.VES, 40m,
            new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "manual");

        Assert.Throws<DomainRuleException>(() => rate.Convert(new Money(1m, Currency.VES), 2));
    }

    [Fact]
    public void Exchange_rate_requires_distinct_currencies_and_a_source()
    {
        var utc = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => ExchangeRate.Create(Currency.USD, Currency.USD, 1m, utc, "manual"));
        Assert.Throws<ArgumentException>(() => ExchangeRate.Create(Currency.USD, Currency.VES, 1m, utc, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExchangeRate.Create((Currency)999, Currency.VES, 1m, utc, "manual"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExchangeRate.Create(Currency.USD, (Currency)999, 1m, utc, "manual"));
    }

    [Fact]
    public void Money_subtraction_rejects_different_currencies()
    {
        Assert.Throws<DomainRuleException>(() => _ = new Money(2m, Currency.USD) - new Money(1m, Currency.VES));
    }
}
