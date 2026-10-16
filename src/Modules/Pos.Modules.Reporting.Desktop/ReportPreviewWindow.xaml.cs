using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pos.Desktop.Modules.Abstractions;
using Pos.Shared.Contracts;

namespace Pos.Modules.Reporting.Desktop;

public partial class ReportPreviewWindow : Window
{
    private readonly string _filterSummary;
    private readonly IReadOnlyList<PosDesktopReportColumn> _definitions;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows;
    private readonly ReportSheetTemplate _template;
    private readonly IReadOnlyList<ReportSummaryMetric> _summaryMetrics;
    private bool _uiReady;

    public ReportPreviewWindow(
        string reportTitle,
        string filterSummary,
        IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        ReportSheetTemplate? template = null,
        IReadOnlyList<ReportSummaryMetric>? summaryMetrics = null)
    {
        InitializeComponent();
        _filterSummary = filterSummary;
        _definitions = columns;
        _rows = rows;
        _summaryMetrics = summaryMetrics ?? [];
        _template = template ?? new ReportSheetTemplate
        {
            Title = reportTitle,
            Columns = columns.Select((column, index) => new ReportSheetColumn(column.Id, column.Header, true, index)).ToArray()
        };

        ReportTitleText.Text = _template.Title;
        ReportCommerceText.Text = _template.CommerceName;
        ReportDetailsText.Text = BuildDetails();
        SelectTag(PaperSizePicker, _template.PageSize == "legal" ? "oficio" : _template.PageSize);
        SelectTag(OrientationPicker, _template.Orientation);
        _uiReady = true;
        UpdateDocument();
    }

    private void PageLayout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady) UpdateDocument();
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
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
            printDialog.PrintDocument(document.DocumentPaginator, _template.Title);
            StatusText.Text = "Reporte enviado a la impresora seleccionada. Para crear un PDF, elige Microsoft Print to PDF.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"No se pudo imprimir el reporte: {exception.Message}";
        }
    }

    private void UpdateDocument()
    {
        var columns = ReportSheetProjection.VisibleColumns(_template, _definitions);
        ReportDocumentViewer.Document = CreateDocument(_template, _filterSummary, columns, _rows, _definitions,
            _summaryMetrics, SelectedPageSize());
        ReportTitleText.Text = _template.Title;
        ReportCommerceText.Text = _template.CommerceName;
        ReportDetailsText.Text = BuildDetails();
    }

    private string BuildDetails()
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(_template.Subtitle)) details.Add(_template.Subtitle);
        if (_template.ShowGeneratedAt) details.Add($"Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}");
        if (_template.ShowFilters && !string.IsNullOrWhiteSpace(_filterSummary)) details.Add(_filterSummary);
        details.Add($"{_rows.Count:N0} filas · {ReportSheetProjection.VisibleColumns(_template, _definitions).Count} columnas");
        return string.Join("   ·   ", details);
    }

    private Size SelectedPageSize()
    {
        var paper = (PaperSizePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var size = paper switch
        {
            "oficio" => new Size(816, 1248),
            "a4" => new Size(793.7, 1122.5),
            _ => new Size(816, 1056)
        };

        return (OrientationPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "portrait"
            ? size
            : new Size(size.Height, size.Width);
    }

    private static FlowDocument CreateDocument(
        ReportSheetTemplate template,
        string filterSummary,
        IReadOnlyList<PosDesktopReportColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sourceRows,
        IReadOnlyList<PosDesktopReportColumn> allDefinitions,
        IReadOnlyList<ReportSummaryMetric> summaryMetrics,
        Size pageSize)
    {
        var pagePadding = new Thickness(34);
        var accent = AccentBrush(template.Accent);
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

        document.Blocks.Add(new Paragraph(new Run(template.CommerceName))
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            Margin = new Thickness(0, 0, 0, 3)
        });
        document.Blocks.Add(new Paragraph(new Run(template.Title))
        {
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            Margin = new Thickness(0, 0, 0, 3)
        });

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(template.Subtitle)) detailParts.Add(template.Subtitle);
        if (template.ShowGeneratedAt) detailParts.Add($"Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}");
        if (template.ShowFilters && !string.IsNullOrWhiteSpace(filterSummary)) detailParts.Add(filterSummary);
        detailParts.Add($"Filas: {sourceRows.Count:N0}");
        document.Blocks.Add(new Paragraph(new Run(string.Join("   ·   ", detailParts)))
        {
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(82, 103, 127)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(190, 202, 217)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Margin = new Thickness(0, 0, 0, 10)
        });

        if (template.ShowSummary && summaryMetrics.Count > 0)
        {
            document.Blocks.Add(new Paragraph(new Run("RESUMEN"))
            {
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var summaryTable = new Table
            {
                CellSpacing = 5,
                BorderThickness = new Thickness(0)
            };
            for (var columnIndex = 0; columnIndex < 3; columnIndex++)
                summaryTable.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

            var summaryRows = new TableRowGroup();
            foreach (var metricRow in summaryMetrics.Chunk(3))
            {
                var row = new TableRow();
                foreach (var metric in metricRow)
                {
                    var paragraph = new Paragraph
                    {
                        Margin = new Thickness(0),
                        FontSize = 8,
                        Foreground = new SolidColorBrush(Color.FromRgb(82, 103, 127))
                    };
                    paragraph.Inlines.Add(new Run(metric.Label) { FontWeight = FontWeights.SemiBold });
                    paragraph.Inlines.Add(new LineBreak());
                    paragraph.Inlines.Add(new Run(metric.Value)
                    {
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = accent
                    });
                    paragraph.Inlines.Add(new LineBreak());
                    if (!string.IsNullOrWhiteSpace(metric.Detail))
                        paragraph.Inlines.Add(new Run(metric.Detail) { FontSize = 7 });
                    row.Cells.Add(new TableCell(paragraph)
                    {
                        Background = LightAccentBrush(template.Accent),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 235)),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(7, 6, 7, 6)
                    });
                }
                for (var blankCount = metricRow.Length; blankCount < 3; blankCount++)
                    row.Cells.Add(new TableCell(new Paragraph(new Run(" ")) { Margin = new Thickness(0) })
                    {
                        BorderThickness = new Thickness(0)
                    });
                summaryRows.Rows.Add(row);
            }
            summaryTable.RowGroups.Add(summaryRows);
            document.Blocks.Add(summaryTable);
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 9) });
        }

        if (columns.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("No hay columnas seleccionadas para esta hoja.")));
            return document;
        }

        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
            BorderThickness = new Thickness(0.5)
        };
        var layouts = template.Columns.ToDictionary(column => column.ColumnId, StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var width = layouts.GetValueOrDefault(column.Id)?.Width ?? 0;
            table.Columns.Add(new TableColumn
            {
                Width = width > 0
                    ? new GridLength(width, GridUnitType.Star)
                    : new GridLength(ColumnWeight(column), GridUnitType.Star)
            });
        }

        var headingGroup = new TableRowGroup();
        var heading = new TableRow();
        foreach (var column in columns)
            heading.Cells.Add(CreateCell(ReportSheetProjection.Header(template, column), Brushes.White, accent,
                FontWeights.Bold, 8, new Thickness(5, 4, 5, 4)));
        headingGroup.Rows.Add(heading);
        table.RowGroups.Add(headingGroup);

        var bodyGroup = new TableRowGroup();
        var previewRows = ReportSheetProjection.BuildRows(template, columns, sourceRows, allDefinitions);
        var detailIndex = 0;
        foreach (var previewRow in previewRows)
        {
            var row = new TableRow();
            if (previewRow.Kind == ReportSheetRowKind.GroupHeader)
            {
                var groupLabel = previewRow.Values.GetValueOrDefault(columns[0].Id);
                row.Cells.Add(CreateCell(ReportSheetProjection.FormatValue(groupLabel), Brushes.White, accent,
                    FontWeights.Bold, 9, new Thickness(6, 5, 6, 5), columns.Count));
            }
            else
            {
                var isSummary = previewRow.Kind is ReportSheetRowKind.Subtotal or ReportSheetRowKind.GrandTotal;
                var background = previewRow.Kind switch
                {
                    ReportSheetRowKind.Subtotal => new SolidColorBrush(Color.FromRgb(234, 240, 247)),
                    ReportSheetRowKind.GrandTotal => new SolidColorBrush(Color.FromRgb(220, 232, 246)),
                    _ => detailIndex++ % 2 == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(244, 247, 250))
                };
                foreach (var column in columns)
                {
                    row.Cells.Add(CreateCell(ReportSheetProjection.FormatValue(previewRow.Values.GetValueOrDefault(column.Id)),
                        background, new SolidColorBrush(Color.FromRgb(37, 49, 64)), isSummary ? FontWeights.Bold : FontWeights.Normal,
                        8, new Thickness(5, 3, 5, 3)));
                }
            }
            bodyGroup.Rows.Add(row);
        }

        if (previewRows.Count == 0)
        {
            var emptyRow = new TableRow();
            emptyRow.Cells.Add(CreateCell("No hay resultados para los filtros seleccionados.", Brushes.White,
                new SolidColorBrush(Color.FromRgb(82, 103, 127)), FontWeights.Normal, 9, new Thickness(8), columns.Count));
            bodyGroup.Rows.Add(emptyRow);
        }

        table.RowGroups.Add(bodyGroup);
        document.Blocks.Add(table);
        if (!string.IsNullOrWhiteSpace(template.FooterText))
        {
            document.Blocks.Add(new Paragraph(new Run(template.FooterText))
            {
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromRgb(82, 103, 127)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(190, 202, 217)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 7, 0, 0),
                Margin = new Thickness(0, 10, 0, 0)
            });
        }
        return document;
    }

    private static TableCell CreateCell(string text, Brush background, Brush foreground, FontWeight weight,
        double fontSize, Thickness padding, int columnSpan = 1)
        => new(new Paragraph(new Run(text))
        {
            Margin = new Thickness(0),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = foreground
        })
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
            BorderThickness = new Thickness(0, 0, 0.5, 0.5),
            Padding = padding,
            ColumnSpan = columnSpan
        };

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

    private static Brush AccentBrush(string accent) => accent switch
    {
        "green" => new SolidColorBrush(Color.FromRgb(36, 113, 75)),
        "charcoal" => new SolidColorBrush(Color.FromRgb(57, 66, 78)),
        _ => new SolidColorBrush(Color.FromRgb(23, 63, 120))
    };

    private static Brush LightAccentBrush(string accent)
    {
        var color = ((SolidColorBrush)AccentBrush(accent)).Color;
        static byte Blend(byte component) => (byte)Math.Round(component * 0.1 + 255 * 0.9);
        return new SolidColorBrush(Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B)));
    }

    private static void SelectTag(ComboBox picker, string tag)
        => picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? picker.Items[0];
}
