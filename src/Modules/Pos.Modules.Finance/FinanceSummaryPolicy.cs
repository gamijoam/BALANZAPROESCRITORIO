namespace Pos.Modules.Finance;

public sealed record FinanceOpenPayableInfo(Guid InvoiceId, string SupplierName, string DocumentType,
    string DocumentNumber, string? ControlNumber, string Currency, DateOnly IssueDate, DateOnly? DueDate,
    decimal InvoiceAmount, decimal PaidAmount, decimal Balance, string Status, string PaymentTerm);

public sealed record FinanceSummaryBalanceInfo(string Currency, decimal Amount, long Invoices);

public sealed record FinanceSummaryInfo(string Label, IReadOnlyList<FinanceSummaryBalanceInfo> Balances,
    IReadOnlyList<FinanceOpenPayableInfo> Obligations);

public static class FinanceSummaryPolicy
{
    public static FinanceSummaryInfo Build(IEnumerable<FinanceOpenPayableInfo> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var obligations = source
            .Where(item => item.Status is ("Posted" or "Partial" or "Paid") && item.Balance > 0m)
            .OrderBy(item => item.DueDate.HasValue ? 0 : 1)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.IssueDate)
            .ThenBy(item => item.SupplierName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DocumentNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var balances = obligations
            .GroupBy(item => item.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new FinanceSummaryBalanceInfo(group.Key, group.Sum(item => item.Balance), group.LongCount()))
            .ToArray();

        return new("Preliminar · cuentas por pagar", balances, obligations);
    }
}
