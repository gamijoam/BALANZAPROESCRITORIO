using System.Globalization;
using Pos.Desktop.Modules.Abstractions;
using Pos.Shared.Contracts;

namespace Pos.Modules.Reporting.Desktop;

public enum ReportSheetRowKind
{
    Detail,
    GroupHeader,
    Subtotal,
    GrandTotal
}

internal sealed record ReportSheetPreviewRow(ReportSheetRowKind Kind,
    IReadOnlyDictionary<string, object?> Values);

internal static class ReportSheetProjection
{
    public static IReadOnlyList<PosDesktopReportColumn> VisibleColumns(ReportSheetTemplate template,
        IReadOnlyList<PosDesktopReportColumn> definitions)
    {
        if (template.Columns.Count == 0)
            return definitions.Where(column => column.VisibleByDefault).ToArray();

        var layouts = template.Columns.ToDictionary(column => column.ColumnId, StringComparer.Ordinal);
        return definitions
            .Select((column, index) => (column, index, layout: layouts.GetValueOrDefault(column.Id)))
            .Where(item => item.layout?.Visible == true)
            .OrderBy(item => item.layout?.DisplayOrder ?? item.index)
            .Select(item => item.column)
            .ToArray();
    }

    public static string Header(ReportSheetTemplate template, PosDesktopReportColumn column)
        => template.Columns.FirstOrDefault(item => item.ColumnId == column.Id)?.Header is { Length: > 0 } header
            ? header
            : column.Header;

    public static IReadOnlyList<ReportSheetPreviewRow> BuildRows(ReportSheetTemplate template,
        IReadOnlyList<PosDesktopReportColumn> visibleColumns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sourceRows,
        IReadOnlyList<PosDesktopReportColumn>? allDefinitions = null)
    {
        var rows = new List<ReportSheetPreviewRow>();
        if (visibleColumns.Count == 0) return rows;

        var totals = template.SumColumnIds.ToHashSet(StringComparer.Ordinal);
        var groupColumn = (allDefinitions ?? visibleColumns).FirstOrDefault(column => column.Id == template.GroupByColumnId);
        var groups = groupColumn is null
            ? new[] { (Key: (string?)null, Rows: sourceRows) }
            : sourceRows.GroupBy(row => row.GetValueOrDefault(groupColumn.Id), ReportValueComparer.Instance)
                .Select(group => (Key: (string?)FormatValue(group.Key), Rows: (IReadOnlyList<IReadOnlyDictionary<string, object?>>)group.ToArray()))
                .ToArray();

        foreach (var group in groups)
        {
            if (groupColumn is not null)
            {
                rows.Add(new ReportSheetPreviewRow(ReportSheetRowKind.GroupHeader,
                    new Dictionary<string, object?> { [visibleColumns[0].Id] = $"{Header(template, groupColumn)}: {group.Key}" }));
            }

            rows.AddRange(group.Rows.Select(row => new ReportSheetPreviewRow(ReportSheetRowKind.Detail, row)));

            if (groupColumn is not null && totals.Count > 0)
                rows.Add(CreateTotalRow(ReportSheetRowKind.Subtotal, "Subtotal", visibleColumns, group.Rows, totals));
        }

        if (groupColumn is null && totals.Count > 0)
            rows.Add(CreateTotalRow(ReportSheetRowKind.GrandTotal, "Total", visibleColumns, sourceRows, totals));
        else if (groupColumn is not null && totals.Count > 0)
            rows.Add(CreateTotalRow(ReportSheetRowKind.GrandTotal, "Total general", visibleColumns, sourceRows, totals));

        return rows;
    }

    public static string FormatValue(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture),
        DateTimeOffset date => date.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture),
        decimal amount => amount.ToString("N2", CultureInfo.CurrentCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.CurrentCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static ReportSheetPreviewRow CreateTotalRow(ReportSheetRowKind kind, string label,
        IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sourceRows,
        IReadOnlySet<string> totalIds)
    {
        var labelColumn = columns.FirstOrDefault(column => !totalIds.Contains(column.Id)) ?? columns[0];
        var values = new Dictionary<string, object?> { [labelColumn.Id] = label };
        foreach (var column in columns.Where(column => totalIds.Contains(column.Id)))
            values[column.Id] = sourceRows.Sum(row => ToDecimal(row.GetValueOrDefault(column.Id)));
        return new ReportSheetPreviewRow(kind, values);
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        decimal amount => amount,
        byte number => number,
        short number => number,
        int number => number,
        long number => number,
        sbyte number => number,
        ushort number => number,
        uint number => number,
        ulong number => number,
        _ => 0m
    };

    private sealed class ReportValueComparer : IEqualityComparer<object?>
    {
        public static ReportValueComparer Instance { get; } = new();

        public new bool Equals(object? left, object? right)
            => left is string leftText && right is string rightText
                ? StringComparer.CurrentCultureIgnoreCase.Equals(leftText, rightText)
                : object.Equals(left, right);

        public int GetHashCode(object? value)
            => value is string text
                ? StringComparer.CurrentCultureIgnoreCase.GetHashCode(text)
                : value?.GetHashCode() ?? 0;
    }
}
