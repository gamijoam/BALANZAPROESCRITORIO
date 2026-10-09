namespace Pos.Domain.Finance;

/// <summary>Immutable close-of-shift cash count compared with recorded cash activity.</summary>
public sealed record CashReconciliation(decimal ExpectedBalance, decimal CountedBalance, decimal Variance)
{
    public static CashReconciliation Calculate(decimal openingBalance, IEnumerable<decimal> cashReceipts,
        IEnumerable<decimal> cashOutflows, decimal countedBalance)
    {
        ArgumentNullException.ThrowIfNull(cashReceipts);
        ArgumentNullException.ThrowIfNull(cashOutflows);
        if (openingBalance < 0m) throw new ArgumentOutOfRangeException(nameof(openingBalance));
        if (countedBalance < 0m) throw new ArgumentOutOfRangeException(nameof(countedBalance));
        var receipts = cashReceipts.ToArray();
        var outflows = cashOutflows.ToArray();
        if (receipts.Any(x => x < 0m)) throw new ArgumentOutOfRangeException(nameof(cashReceipts));
        if (outflows.Any(x => x < 0m)) throw new ArgumentOutOfRangeException(nameof(cashOutflows));
        var expected = openingBalance + receipts.Sum() - outflows.Sum();
        if (expected < 0m) throw new DomainRuleException("Cash outflows cannot exceed the recorded cash balance.");
        return new CashReconciliation(expected, countedBalance, countedBalance - expected);
    }
}
