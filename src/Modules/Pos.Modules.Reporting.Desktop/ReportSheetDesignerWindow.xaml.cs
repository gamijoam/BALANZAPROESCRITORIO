using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Pos.Desktop.Modules.Abstractions;
using Pos.Shared.Contracts;

namespace Pos.Modules.Reporting.Desktop;

public partial class ReportSheetDesignerWindow : Window
{
    private readonly string _filterSummary;
    private readonly IReadOnlyList<PosDesktopReportColumn> _definitions;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _sourceRows;
    private readonly ObservableCollection<ReportSheetColumnEditorItem> _columnItems = [];
    private bool _ready;

    public ReportSheetDesignerWindow(string reportTitle, string filterSummary,
        IReadOnlyList<PosDesktopReportColumn> definitions,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        ReportSheetTemplate? template = null,
        string? templateName = null)
    {
        InitializeComponent();
        _filterSummary = filterSummary;
        _definitions = definitions;
        _sourceRows = rows;

        template ??= DefaultTemplate(reportTitle, definitions);
        TemplateNameBox.Text = templateName ?? $"Mi hoja - {reportTitle}";
        CommerceNameBox.Text = template.CommerceName;
        TitleBox.Text = template.Title;
        SubtitleBox.Text = template.Subtitle ?? string.Empty;
        FooterTextBox.Text = template.FooterText ?? string.Empty;
        ShowGeneratedAtCheck.IsChecked = template.ShowGeneratedAt;
        ShowFiltersCheck.IsChecked = template.ShowFilters;
        SelectTag(PageSizePicker, template.PageSize);
        SelectTag(OrientationPicker, template.Orientation);
        SelectTag(AccentPicker, template.Accent);

        var storedColumns = template.Columns.ToDictionary(column => column.ColumnId, StringComparer.Ordinal);
        foreach (var (definition, index) in definitions.Select((column, index) => (column, index)))
        {
            var stored = storedColumns.GetValueOrDefault(definition.Id);
            _columnItems.Add(new ReportSheetColumnEditorItem(definition.Id,
                stored?.Header ?? definition.Header,
                stored?.Visible ?? definition.VisibleByDefault,
                stored?.Width ?? 0,
                stored?.DisplayOrder ?? index));
        }
        var orderedColumnItems = _columnItems.OrderBy(item => item.DisplayOrder).ToArray();
        _columnItems.Clear();
        foreach (var item in orderedColumnItems) _columnItems.Add(item);
        NormalizeColumnOrder();
        ColumnsEditor.ItemsSource = _columnItems;
        ConfigureGroupPicker(template.GroupByColumnId);
        ConfigureSumColumns(template.SumColumnIds);
        _ready = true;
        UpdatePreview();
    }

    public ReportSheetTemplate SheetTemplate { get; private set; } = new();
    public bool SaveRequested { get; private set; }
    public string TemplateName => TemplateNameBox.Text.Trim();

    private void ConfigureGroupPicker(string? selectedColumnId)
    {
        GroupByPicker.Items.Clear();
        GroupByPicker.Items.Add(new ComboBoxItem { Content = "Sin agrupación", Tag = string.Empty });
        foreach (var column in _definitions.Where(column => column.ValueType is PosDesktopReportValueType.Text or PosDesktopReportValueType.DateTime))
            GroupByPicker.Items.Add(new ComboBoxItem { Content = column.Header, Tag = column.Id });
        GroupByPicker.SelectedItem = GroupByPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedColumnId ?? string.Empty, StringComparison.Ordinal))
            ?? GroupByPicker.Items[0];
    }

    private void ConfigureSumColumns(IReadOnlyList<string> selectedIds)
    {
        SumColumnsPanel.Children.Clear();
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var column in _definitions.Where(column => column.ValueType is PosDesktopReportValueType.Decimal or PosDesktopReportValueType.Integer))
        {
            var check = new CheckBox
            {
                Content = column.Header,
                Tag = column.Id,
                IsChecked = selected.Contains(column.Id),
                Margin = new Thickness(0, 3, 0, 3)
            };
            check.Checked += TemplateOption_Changed;
            check.Unchecked += TemplateOption_Changed;
            SumColumnsPanel.Children.Add(check);
        }
    }

    private void TemplateText_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void TemplateOption_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void ColumnsEditor_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(UpdatePreview, DispatcherPriority.Background);

    private void MoveColumnUp_Click(object sender, RoutedEventArgs e) => MoveSelectedColumn(-1);

    private void MoveColumnDown_Click(object sender, RoutedEventArgs e) => MoveSelectedColumn(1);

    private void MoveSelectedColumn(int direction)
    {
        if (ColumnsEditor.SelectedItem is not ReportSheetColumnEditorItem item) return;
        var index = _columnItems.IndexOf(item);
        var nextIndex = index + direction;
        if (nextIndex < 0 || nextIndex >= _columnItems.Count) return;
        _columnItems.Move(index, nextIndex);
        NormalizeColumnOrder();
        UpdatePreview();
    }

    private void NormalizeColumnOrder()
    {
        for (var index = 0; index < _columnItems.Count; index++)
            _columnItems[index].DisplayOrder = index;
    }

    private void UpdatePreview()
    {
        if (!_ready) return;
        var template = BuildTemplate();
        PreviewCommerceText.Text = template.CommerceName;
        PreviewTitleText.Text = template.Title;
        PreviewSubtitleText.Text = template.Subtitle ?? string.Empty;
        PreviewSubtitleText.Visibility = string.IsNullOrWhiteSpace(template.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        PreviewDetailsText.Text = string.Join("   ·   ", new[]
        {
            template.ShowGeneratedAt ? $"Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}" : null,
            template.ShowFilters && !string.IsNullOrWhiteSpace(_filterSummary) ? _filterSummary : null,
            $"{_sourceRows.Count:N0} filas"
        }.Where(value => value is not null));

        var accent = AccentBrush(template.Accent);
        PreviewCommerceText.Foreground = accent;
        PreviewTitleText.Foreground = accent;
        PreviewAccentBar.Background = accent;
        SheetPreviewGrid.Resources[typeof(DataGridColumnHeader)] = new Style(typeof(DataGridColumnHeader))
        {
            Setters =
            {
                new Setter(BackgroundProperty, accent),
                new Setter(ForegroundProperty, Brushes.White),
                new Setter(FontWeightProperty, FontWeights.Bold),
                new Setter(PaddingProperty, new Thickness(9, 7, 9, 7)),
                new Setter(BorderBrushProperty, Brushes.White)
            }
        };

        var visibleColumns = ReportSheetProjection.VisibleColumns(template, _definitions);
        SheetPreviewGrid.Columns.Clear();
        foreach (var column in visibleColumns)
        {
            var layout = template.Columns.First(item => item.ColumnId == column.Id);
            SheetPreviewGrid.Columns.Add(new DataGridTextColumn
            {
                Header = layout.Header,
                Binding = new Binding($"Values[{column.Id}]")
                {
                    StringFormat = column.ValueType switch
                    {
                        PosDesktopReportValueType.Decimal => "N2",
                        PosDesktopReportValueType.DateTime => "dd/MM/yyyy HH:mm",
                        _ => null
                    }
                },
                Width = layout.Width > 0
                    ? new DataGridLength(layout.Width)
                    : column.ValueType == PosDesktopReportValueType.Text
                        ? new DataGridLength(1, DataGridLengthUnitType.Star)
                        : DataGridLength.SizeToHeader
            });
        }

        SheetPreviewGrid.ItemsSource = ReportSheetProjection.BuildRows(template, visibleColumns, _sourceRows, _definitions);
        DesignerStatusText.Text = visibleColumns.Count == 0
            ? "Activa al menos una columna para mostrar la hoja."
            : $"Vista previa · {visibleColumns.Count} columnas · {_sourceRows.Count:N0} filas · zoom 100 %";
    }

    private ReportSheetTemplate BuildTemplate()
    {
        var selectedGroup = GroupByPicker.SelectedItem as ComboBoxItem;
        var sumColumns = SumColumnsPanel.Children.OfType<CheckBox>()
            .Where(check => check.IsChecked == true && check.Tag is string)
            .Select(check => (string)check.Tag).ToArray();
        return new ReportSheetTemplate
        {
            CommerceName = CommerceNameBox.Text.Trim(),
            Title = TitleBox.Text.Trim(),
            Subtitle = NullIfBlank(SubtitleBox.Text),
            ShowGeneratedAt = ShowGeneratedAtCheck.IsChecked == true,
            ShowFilters = ShowFiltersCheck.IsChecked == true,
            GroupByColumnId = NullIfBlank(selectedGroup?.Tag?.ToString()),
            SumColumnIds = sumColumns,
            Columns = _columnItems.Select(item => new ReportSheetColumn(item.ColumnId,
                item.Header.Trim(), item.Visible, item.DisplayOrder, item.Width)).ToArray(),
            Accent = SelectedTag(AccentPicker, "blue"),
            PageSize = SelectedTag(PageSizePicker, "a4"),
            Orientation = SelectedTag(OrientationPicker, "landscape"),
            FooterText = NullIfBlank(FooterTextBox.Text.ReplaceLineEndings(" "))
        };
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        ColumnsEditor.CommitEdit(DataGridEditingUnit.Cell, true);
        ColumnsEditor.CommitEdit(DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(TemplateNameBox.Text))
        {
            DesignerStatusText.Text = "Escribe un nombre para guardar esta plantilla.";
            TemplateNameBox.Focus();
            return;
        }
        if (!ValidateTemplate()) return;
        SheetTemplate = BuildTemplate();
        SaveRequested = true;
        DialogResult = true;
    }

    private void PrintPreview_Click(object sender, RoutedEventArgs e)
    {
        ColumnsEditor.CommitEdit(DataGridEditingUnit.Cell, true);
        ColumnsEditor.CommitEdit(DataGridEditingUnit.Row, true);
        if (!ValidateTemplate()) return;
        SheetTemplate = BuildTemplate();
        DialogResult = true;
    }

    private bool ValidateTemplate()
    {
        if (!_columnItems.Any(column => column.Visible))
        {
            DesignerStatusText.Text = "Activa al menos una columna para continuar.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(CommerceNameBox.Text) || string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            DesignerStatusText.Text = "Indica el nombre del comercio y el título del reporte.";
            return false;
        }
        if (_columnItems.Any(column => string.IsNullOrWhiteSpace(column.Header) || column.Header.Length > 96 ||
                column.Header.Any(char.IsControl) || column.Width is < 0 or > 1200))
        {
            DesignerStatusText.Text = "Revisa los títulos de columna y sus anchos (deja 0 para usar el automático).";
            return false;
        }
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static ReportSheetTemplate DefaultTemplate(string title, IReadOnlyList<PosDesktopReportColumn> columns)
        => new()
        {
            CommerceName = "BALANZAPRO",
            Title = title,
            ShowGeneratedAt = true,
            ShowFilters = true,
            Columns = columns.Select((column, index) => new ReportSheetColumn(column.Id, column.Header,
                column.VisibleByDefault, index)).ToArray()
        };

    private static void SelectTag(ComboBox picker, string tag)
        => picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? picker.Items[0];

    private static string SelectedTag(ComboBox picker, string fallback)
        => (picker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Brush AccentBrush(string accent) => accent switch
    {
        "green" => new SolidColorBrush(Color.FromRgb(36, 113, 75)),
        "charcoal" => new SolidColorBrush(Color.FromRgb(57, 66, 78)),
        _ => new SolidColorBrush(Color.FromRgb(23, 63, 120))
    };
}

internal sealed class ReportSheetColumnEditorItem(string columnId, string header, bool visible, int width, int displayOrder)
{
    public string ColumnId { get; } = columnId;
    public string Header { get; set; } = header;
    public bool Visible { get; set; } = visible;
    public int Width { get; set; } = width;
    public int DisplayOrder { get; set; } = displayOrder;
}
