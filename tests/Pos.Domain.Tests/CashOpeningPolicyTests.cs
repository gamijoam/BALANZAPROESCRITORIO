using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class CashOpeningPolicyTests
{
    [Fact]
    public void Opening_in_usd_keeps_the_initial_usd_count_but_sets_the_session_currency_to_ves()
    {
        var opening = CashOpeningPolicy.Resolve(75m, "USD", null, null);

        Assert.Equal("VES", opening.Currency);
        Assert.Equal(75m, opening.OpeningUsd);
        Assert.Equal(0m, opening.OpeningVes);
    }

    [Fact]
    public void Opening_with_both_currency_balances_preserves_them_separately_and_uses_ves()
    {
        var opening = CashOpeningPolicy.Resolve(null, "VES", 20m, 500m);

        Assert.Equal("VES", opening.Currency);
        Assert.Equal(20m, opening.OpeningUsd);
        Assert.Equal(500m, opening.OpeningVes);
    }

    [Fact]
    public void Opening_rejects_negative_starting_cash()
    {
        Assert.Throws<DomainRuleException>(() => CashOpeningPolicy.Resolve(null, "VES", -1m, 0m));
    }
}
