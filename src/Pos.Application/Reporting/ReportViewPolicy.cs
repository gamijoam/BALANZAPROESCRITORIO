using System.Text.RegularExpressions;
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
        return null;
    }
}
