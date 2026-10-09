using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class CashReconciliationTests
{
    [Fact]
    public void Expected_cash_is_opening_balance_plus_cash_receipts_and_movements()
    {
        var reconciliation = CashReconciliation.Calculate(100m, [25m, 5m], [10m], 115m);

        Assert.Equal(120m, reconciliation.ExpectedBalance);
        Assert.Equal(115m, reconciliation.CountedBalance);
        Assert.Equal(-5m, reconciliation.Variance);
    }

    [Fact]
    public void Reconciliation_rejects_negative_balances_and_cash_movements()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CashReconciliation.Calculate(-1m, [], [], 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => CashReconciliation.Calculate(0m, [-1m], [], 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => CashReconciliation.Calculate(0m, [], [-1m], 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => CashReconciliation.Calculate(0m, [], [], -1m));
    }

    [Fact]
    public void Cash_reconciliation_rejects_outflows_greater_than_recorded_cash()
    {
        Assert.Throws<DomainRuleException>(() => CashReconciliation.Calculate(5m, [], [6m], 0m));
    }
}
