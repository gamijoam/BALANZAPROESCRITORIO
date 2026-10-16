using System.Text.RegularExpressions;
using System.Text.Json;
using Pos.Shared.Contracts;

namespace Pos.Application.Reporting;

public static partial class ReportViewPolicy
{
    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportIdPattern();

    public static bool IsValidReportId(string? reportId)
        => !string.IsNullOrWhiteSpace(reportId) && ReportIdPattern().IsMatch(reportId);

    public static string? Validate(string? reportId, SavedReportViewDefinition? view)
    {
        if (!IsValidReportId(reportId)) return "El identificador del reporte no es válido.";
        if (view is null || string.IsNullOrWhiteSpace(view.Name) || view.Name.Trim().Length > 80 || view.Name.Any(char.IsControl))
            return "El nombre debe contener entre 1 y 80 caracteres.";
        if (view.Columns is null || view.Columns.Count > 100 || view.Columns.Any(item =>
                string.IsNullOrWhiteSpace(item.ColumnId) || item.ColumnId.Length > 96 || item.DisplayOrder < 0) ||
            view.Columns.Select(item => item.ColumnId).Distinct(StringComparer.Ordinal).Count() != view.Columns.Count ||
            view.Columns.Select(item => item.DisplayOrder).Distinct().Count() != view.Columns.Count)
            return "La lista de columnas contiene datos inválidos o repetidos.";
        if (view.Filters is null || view.Filters.Count > 40 || view.Filters.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 64 || item.Key.Any(char.IsControl) ||
                item.Value is null || item.Value.Length > 256 || item.Value.Any(char.IsControl)))
            return "Los filtros guardados superan el límite permitido o contienen datos inválidos.";
        if (view.Sheet is { } sheet && !IsValidSheet(sheet))
            return "El diseño de la hoja contiene opciones inválidas o supera los límites permitidos.";
        return null;
    }

    private static bool IsValidSheet(ReportSheetTemplate sheet)
    {
        if (sheet.Version != 1 || string.IsNullOrWhiteSpace(sheet.CommerceName) || sheet.CommerceName.Length > 120 ||
            sheet.CommerceName.Any(char.IsControl) || string.IsNullOrWhiteSpace(sheet.Title) || sheet.Title.Length > 120 ||
            sheet.Title.Any(char.IsControl) || sheet.Subtitle?.Length > 250 || sheet.Subtitle?.Any(char.IsControl) == true ||
            sheet.FooterText?.Length > 300 || sheet.FooterText?.Any(char.IsControl) == true ||
            sheet.Columns is null || sheet.Columns.Count > 100 || sheet.SumColumnIds is null || sheet.SumColumnIds.Count > 40 ||
            sheet.Accent is not ("blue" or "green" or "charcoal") ||
            sheet.PageSize is not ("letter" or "legal" or "a4") ||
            sheet.Orientation is not ("landscape" or "portrait")) return false;

        if (sheet.Columns.Any(column => string.IsNullOrWhiteSpace(column.ColumnId) || column.ColumnId.Length > 96 ||
                column.ColumnId.Any(char.IsControl) || string.IsNullOrWhiteSpace(column.Header) || column.Header.Length > 96 ||
                column.Header.Any(char.IsControl) || column.DisplayOrder < 0 || column.Width is < 0 or > 1200) ||
            sheet.Columns.Select(column => column.ColumnId).Distinct(StringComparer.Ordinal).Count() != sheet.Columns.Count ||
            sheet.Columns.Select(column => column.DisplayOrder).Distinct().Count() != sheet.Columns.Count ||
            sheet.SumColumnIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(char.IsControl)) ||
            sheet.SumColumnIds.Distinct(StringComparer.Ordinal).Count() != sheet.SumColumnIds.Count ||
            sheet.GroupByColumnId is { } groupId && (groupId.Length > 96 || groupId.Any(char.IsControl))) return false;

        return JsonSerializer.SerializeToUtf8Bytes(sheet).Length <= 64 * 1024;
    }
}
