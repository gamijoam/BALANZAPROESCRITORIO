using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Pos.Desktop.Modules.Abstractions;

namespace Pos.Modules.Reporting.Desktop;

public partial class ReportPreviewWindow : Window
{
    private readonly string _reportTitle;
    private readonly string _filterSummary;
    private readonly IReadOnlyList<PosDesktopReportColumn> _columns;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows;
    private bool _uiReady;

    public ReportPreviewWindow(
        string reportTitle,
        string filterSummary,
        IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        InitializeComponent();
        _reportTitle = reportTitle;
        _filterSummary = filterSummary;
        _columns = columns;
        _rows = rows;
        ReportTitleText.Text = reportTitle;
        ReportDetailsText.Text = string.IsNullOrWhiteSpace(filterSummary)
            ? $"{rows.Count:N0} filas · {columns.Count} columnas · Generado el {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}"
            : $"{filterSummary}   ·   {rows.Count:N0} filas   ·   Generado el {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}";
        _uiReady = true;
        UpdateDocument();
    }

    private void PageLayout_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_uiReady) UpdateDocument();
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printDialog = new System.Windows.Controls.PrintDialog();
        if (printDialog.ShowDialog() != true) return;

        try
        {
            if (ReportDocumentViewer.Document is not IDocumentPaginatorSource document)
            {
                StatusText.Text = "No se pudo preparar la hoja para imprimir.";
                return;
            }

            var pageSize = SelectedPageSize();
            var printTicket = printDialog.PrintTicket ?? new PrintTicket();
            printTicket.PageOrientation = pageSize.Width >= pageSize.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
            printTicket.PageMediaSize = new PageMediaSize(pageSize.Width, pageSize.Height);
            printDialog.PrintTicket = printTicket;
            printDialog.PrintDocument(document.DocumentPaginator, _reportTitle);
            StatusText.Text = "Reporte enviado a la impresora seleccionada. Para crear un PDF, elige Microsoft Print to PDF.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"No se pudo imprimir el reporte: {exception.Message}";
        }
    }

    private void UpdateDocument()
        => ReportDocumentViewer.Document = CreateDocument(_reportTitle, _filterSummary, _columns, _rows, SelectedPageSize());

    private Size SelectedPageSize()
    {
        var paper = (PaperSizePicker.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        var size = paper switch
        {
            "oficio" => new Size(816, 1248),
            "a4" => new Size(793.7, 1122.5),
            _ => new Size(816, 1056)
        };

        return (OrientationPicker.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "portrait"
            ? size
            : new Size(size.Height, size.Width);
    }

    private static FlowDocument CreateDocument(
        string reportTitle,
        string filterSummary,
        IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Size pageSize)
    {
        var pagePadding = new Thickness(34);
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            PageWidth = pageSize.Width,
            PageHeight = pageSize.Height,
            PagePadding = pagePadding,
            ColumnWidth = Math.Max(1, pageSize.Width - pagePadding.Left - pagePadding.Right),
            Background = Brushes.White
        };

        document.Blocks.Add(new Paragraph(new Run(reportTitle))
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 63, 120)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var details = string.IsNullOrWhiteSpace(filterSummary)
            ? $"Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}   ·   Filas: {rows.Count:N0}"
            : $"{filterSummary}   ·   Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}   ·   Filas: {rows.Count:N0}";
        document.Blocks.Add(new Paragraph(new Run(details))
        {
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(82, 103, 127)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(190, 202, 217)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Margin = new Thickness(0, 0, 0, 10)
        });

        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
            BorderThickness = new Thickness(0.5)
        };
        foreach (var column in columns)
            table.Columns.Add(new TableColumn { Width = new GridLength(ColumnWeight(column), GridUnitType.Star) });

        var headingGroup = new TableRowGroup();
        var heading = new TableRow();
        foreach (var column in columns)
        {
            heading.Cells.Add(new TableCell(new Paragraph(new Run(column.Header))
            {
                Margin = new Thickness(0),
                FontWeight = FontWeights.Bold,
                FontSize = 8,
                Foreground = Brushes.White
            })
            {
                Background = new SolidColorBrush(Color.FromRgb(57, 66, 78)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(5, 4, 5, 4)
            });
        }
        headingGroup.Rows.Add(heading);
        table.RowGroups.Add(headingGroup);

        var bodyGroup = new TableRowGroup();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new TableRow();
            foreach (var column in columns)
            {
                row.Cells.Add(new TableCell(new Paragraph(new Run(FormatValue(rows[rowIndex].GetValueOrDefault(column.Id))))
                {
                    Margin = new Thickness(0),
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Color.FromRgb(37, 49, 64))
                })
                {
                    Background = rowIndex % 2 == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(244, 247, 250)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(219, 226, 235)),
                    BorderThickness = new Thickness(0, 0, 0.5, 0.5),
                    Padding = new Thickness(5, 3, 5, 3)
                });
            }
            bodyGroup.Rows.Add(row);
        }

        if (rows.Count == 0)
        {
            var emptyRow = new TableRow();
            emptyRow.Cells.Add(new TableCell(new Paragraph(new Run("No hay resultados para los filtros seleccionados."))
            {
                Margin = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(82, 103, 127))
            })
            {
                ColumnSpan = columns.Count,
                Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 226, 235)),
                BorderThickness = new Thickness(0, 0, 0.5, 0.5)
            });
            bodyGroup.Rows.Add(emptyRow);
        }

        table.RowGroups.Add(bodyGroup);
        document.Blocks.Add(table);
        return document;
    }

    private static double ColumnWeight(PosDesktopReportColumn column)
    {
        var headerWeight = Math.Clamp(column.Header.Length / 12d, 1d, 2.5d);
        return column.ValueType switch
        {
            PosDesktopReportValueType.DateTime => Math.Max(1.5d, headerWeight),
            PosDesktopReportValueType.Decimal => Math.Max(1.2d, headerWeight),
            _ => headerWeight
        };
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture),
        DateTimeOffset date => date.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture),
        decimal amount => amount.ToString("N2", CultureInfo.CurrentCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.CurrentCulture) ?? "",
        _ => value.ToString() ?? ""
    };
}
