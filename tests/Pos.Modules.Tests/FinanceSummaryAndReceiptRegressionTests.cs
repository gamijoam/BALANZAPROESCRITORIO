using System.Data;
using System.Reflection;
using Pos.Modules.Finance;

namespace Pos.Modules.Tests;

public sealed class FinanceSummaryAndReceiptRegressionTests
{
    [Fact]
    public async Task Receipt_replay_lookup_closes_its_reader_before_the_next_database_command()
    {
        var readerType = typeof(FinanceModule).Assembly.GetType("Pos.Modules.Finance.FinanceReceiptReplayReader");
        Assert.NotNull(readerType);
        var method = readerType!.GetMethod("ReadAndDisposeAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        using var table = new DataTable();
        table.Columns.Add("id", typeof(Guid));
        table.Columns.Add("idempotency_hash", typeof(string));
        table.Columns.Add("completed_at_utc", typeof(DateTime));
        table.Columns.Add("reversed_at_utc", typeof(DateTime));
        using var reader = table.CreateDataReader();

        var task = (Task)method!.Invoke(null, [reader, CancellationToken.None])!;
        await task;

        Assert.Null(task.GetType().GetProperty("Result")!.GetValue(task));
        Assert.True(reader.IsClosed);
    }

    [Fact]
    public void Finance_summary_includes_invoice_obligations_as_well_as_currency_totals()
    {
        var summary = FinanceSummaryPolicy.Build([
            new(Guid.NewGuid(), "Proveedor A", "Invoice", "A-100", null, "USD", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), 100m, 30m, 70m, "Partial", "Credit"),
            new(Guid.NewGuid(), "Proveedor B", "DebitNote", "B-200", "CTRL-2", "USD", new DateOnly(2026, 10, 2), null, 50m, 0m, 50m, "Posted", "Immediate"),
            new(Guid.NewGuid(), "Proveedor C", "Invoice", "C-300", null, "VES", new DateOnly(2026, 10, 3), null, 500m, 0m, 500m, "Posted", "Credit"),
            new(Guid.NewGuid(), "No incluir borrador", "Invoice", "D-400", null, "USD", new DateOnly(2026, 10, 4), null, 10m, 0m, 10m, "Draft", "Credit"),
            new(Guid.NewGuid(), "No incluir sin saldo", "Invoice", "E-500", null, "USD", new DateOnly(2026, 10, 5), null, 20m, 20m, 0m, "Paid", "Credit")
        ]);

        Assert.Equal(3, summary.Obligations.Count);
        Assert.Equal(120m, Assert.Single(summary.Balances, balance => balance.Currency == "USD").Amount);
        Assert.Equal(1, Assert.Single(summary.Balances, balance => balance.Currency == "VES").Invoices);
        Assert.Contains(summary.Obligations, item => item.SupplierName == "Proveedor A" && item.DocumentNumber == "A-100" && item.PaidAmount == 30m && item.Balance == 70m);
        Assert.Contains(summary.Obligations, item => item.SupplierName == "Proveedor B" && item.ControlNumber == "CTRL-2");
    }
}
