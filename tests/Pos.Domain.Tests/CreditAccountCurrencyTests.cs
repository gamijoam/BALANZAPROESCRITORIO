using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class CreditAccountCurrencyTests
{
    [Fact]
    public void Ves_credit_is_stored_as_usd_at_the_rate_used_for_the_sale()
    {
        Assert.Equal(6m, CreditAccountCurrency.ToUsd(5253.90m, "VES", 875.65m));
        Assert.Equal(1m, CreditAccountCurrency.ToUsd(875.65m, "VES", 875.65m));
    }

    [Fact]
    public void Ves_abono_reduces_usd_balance_and_balance_can_be_shown_at_current_rate()
    {
        Assert.Equal(0.60m, CreditAccountCurrency.ToUsd(525.39m, "VES", 875.65m));
        Assert.Equal(5253.90m, CreditAccountCurrency.ToVes(6m, 875.65m));
        Assert.Equal(0.00114201m, CreditAccountCurrency.UsdPerVes(875.65m));
    }
}
