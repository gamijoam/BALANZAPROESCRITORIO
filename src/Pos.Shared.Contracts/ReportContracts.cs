namespace Pos.Shared.Contracts;

public sealed record ReportColumnLayout(string ColumnId, bool Visible, int DisplayOrder);
public sealed record SavedReportViewDefinition(string Name, IReadOnlyList<ReportColumnLayout> Columns,
    IReadOnlyDictionary<string, string> Filters);
public sealed record SavedReportViewInfo(Guid Id, string ReportId, string Name,
    IReadOnlyList<ReportColumnLayout> Columns, IReadOnlyDictionary<string, string> Filters, DateTime UpdatedAtUtc);
public sealed record SaveReportViewRequest(string ReportId, SavedReportViewDefinition View);
public sealed record InventoryMovementReportRow(Guid Id, DateTime OccurredAtUtc, string Sku, string ProductName,
    decimal QuantityDelta, string Reason, string Actor, Guid TerminalId, string? Reference = null);
