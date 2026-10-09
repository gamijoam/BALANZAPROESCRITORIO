using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class CashSessionLedgerTests
{
    [Fact]
    public void Zero_opening_still_counts_cash_sales_and_keeps_currencies_separate()
    {
        var result = CashSessionLedger.Calculate(0m, 0m,
            [new CashReceipt("USD", 10m), new CashReceipt("VES", 1_250m)],
            [new CashMovement("VES", "Out", 250m)],
            countedUsd: 10m, countedVes: 1_000m);

        Assert.Equal(10m, result.Usd.ExpectedBalance);
        Assert.Equal(10m, result.Usd.CountedBalance);
        Assert.Equal(0m, result.Usd.Variance);
        Assert.Equal(1_000m, result.Ves.ExpectedBalance);
        Assert.Equal(1_000m, result.Ves.CountedBalance);
        Assert.Equal(0m, result.Ves.Variance);
    }

    [Fact]
    public void Cash_report_expected_balances_include_zero_opening_sales_until_close()
    {
        var expectedUsd = CashSessionLedger.ExpectedBalance(0m,
            [new CashReceipt("USD", 5m), new CashReceipt("VES", 180m)], [], "USD");
        var expectedVes = CashSessionLedger.ExpectedBalance(0m,
            [new CashReceipt("USD", 5m), new CashReceipt("VES", 180m)], [], "VES");

        Assert.Equal(5m, expectedUsd);
        Assert.Equal(180m, expectedVes);
    }
}
