namespace Pos.Shared.Contracts;

public sealed record ReportColumnLayout(string ColumnId, bool Visible, int DisplayOrder);
public sealed record SavedReportViewDefinition(string Name, IReadOnlyList<ReportColumnLayout> Columns,
    IReadOnlyDictionary<string, string> Filters);
public sealed record SavedReportViewInfo(Guid Id, string ReportId, string Name,
    IReadOnlyList<ReportColumnLayout> Columns, IReadOnlyDictionary<string, string> Filters, DateTime UpdatedAtUtc);
public sealed record SaveReportViewRequest(string ReportId, SavedReportViewDefinition View);
public sealed record InventoryMovementReportRow(Guid Id, DateTime OccurredAtUtc, string Sku, string ProductName,
    decimal QuantityDelta, string Reason, string Actor, Guid TerminalId, string? Reference = null);

public sealed record ReceivableBalanceReportPage(int Page, int PageSize, int TotalCount, decimal? CurrentUsdToVesRate,
    IReadOnlyList<ReceivableBalanceReportRow> Customers);

public sealed record ReceivableBalanceReportRow(Guid CustomerId, string CustomerName, decimal CreditLimitUsd,
    decimal BalanceUsd, decimal AvailableCreditUsd, decimal? BalanceVesEquivalent, DateTime? LastMovementAtUtc);

public sealed record ReceivableMovementReportPage(int Page, int PageSize, int TotalCount,
    IReadOnlyList<ReceivableMovementReportRow> Movements);

public sealed record ReceivableMovementReportRow(Guid Id, Guid CustomerId, string CustomerName, Guid? SaleId,
    string EntryType, decimal AmountUsd, decimal? OriginalAmount, string? OriginalCurrency,
    decimal? AppliedExchangeRate, string Reference, string OperatorName, Guid TerminalId, DateTime OccurredAtUtc);
