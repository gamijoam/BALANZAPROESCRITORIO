using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Modules.Reporting.Desktop;

public partial class ReportCenterWindow : Window
{
    private readonly PosDesktopModuleContext _context;
    private IReadOnlyList<PosDesktopReportSourceInstance> _sources = [];
    private PosDesktopReportSourceInstance? _selectedSource;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows = [];
    private IReadOnlyList<SavedReportViewInfo> _savedViews = [];
    private readonly Dictionary<DataGridColumn, string> _columnIds = [];
    private bool _loading;
    private bool _uiReady;

    public ReportCenterWindow(PosDesktopModuleContext context)
    {
        InitializeComponent();
        _context = context;
        _uiReady = true;
        ColumnPickerPopup.PlacementTarget = ColumnPickerToggle;
        FromDate.SelectedDate = DateTime.Today.AddDays(-30);
        ToDate.SelectedDate = DateTime.Today;
        ReportGrid.Sorting += (_, _) => Dispatcher.BeginInvoke(ApplyVisibleResultCount);
        ReportGrid.ColumnReordered += (_, _) => UpdateColumnOrderInPicker();
        ColumnPickerPopup.Closed += (_, _) => ColumnPickerToggle.IsChecked = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _sources = _context.GetReportSources?.Invoke()
            .Where(item => item.Context.GrantedPermissions.Contains(item.Source.RequiredPermission))
            .OrderBy(item => item.Source.Title, StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [];
        ReportPicker.ItemsSource = _sources;
        if (_sources.Count == 0)
        {
            StatusText.Text = "No hay fuentes de reportes activas para tus permisos.";
            return;
        }
        ReportPicker.SelectedIndex = 0;
        SearchBox.Focus();
    }

    private async void ReportPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ReportPicker.SelectedItem is not PosDesktopReportSourceInstance selected) return;
        _selectedSource = selected;
        _loading = true;
        try
        {
            await LoadSavedViewsAsync();
            ApplyColumnLayout(null);
            await RefreshReportAsync();
        }
        finally { _loading = false; }
    }

    private async Task LoadSavedViewsAsync()
    {
        if (_selectedSource is null || _context.ReportApiClient is null) return;
        try
        {
            _savedViews = await _context.ReportApiClient.GetReportViewsAsync(_selectedSource.Source.Id);
            SavedViewsPicker.ItemsSource = _savedViews;
            SavedViewsPicker.SelectedIndex = -1;
            DeleteViewButton.IsEnabled = false;
        }
        catch (Exception exception) { StatusText.Text = $"No se pudieron cargar tus vistas: {PosUserMessage.ForException(exception)}"; }
    }

    private async void SavedViewsPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SavedViewsPicker.SelectedItem is not SavedReportViewInfo view || _selectedSource is null) return;
        _loading = true;
        try
        {
            ApplyColumnLayout(view.Columns);
            ViewNameBox.Text = view.Name;
            if (view.Filters.TryGetValue("from", out var from) && DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFrom))
                FromDate.SelectedDate = parsedFrom;
            if (view.Filters.TryGetValue("to", out var to) && DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTo))
                ToDate.SelectedDate = parsedTo;
            if (view.Filters.TryGetValue("currency", out var currency))
                CurrencyPicker.SelectedItem = CurrencyPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == currency);
            else CurrencyPicker.SelectedIndex = 0;
            SearchBox.Text = view.Filters.GetValueOrDefault("search", "");
            DeleteViewButton.IsEnabled = true;
            await RefreshReportAsync(view.Filters);
        }
        finally { _loading = false; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private async Task RefreshReportAsync(IReadOnlyDictionary<string, string>? savedFilters = null)
    {
        if (_selectedSource is null) return;
        try
        {
            SetBusy(true);
            var supportsRange = _selectedSource.Source.SupportsDateRange;
            var from = supportsRange ? LocalDateStartUtc(FromDate.SelectedDate ?? DateTime.Today.AddDays(-30)) : (DateTime?)null;
            var to = supportsRange ? LocalDateStartUtc((ToDate.SelectedDate ?? DateTime.Today).AddDays(1)) : (DateTime?)null;
            var filters = new Dictionary<string, string>(savedFilters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            if ((CurrencyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() is { } currency && currency != "Todas") filters["currency"] = currency;
            else filters.Remove("currency");
            var query = new PosDesktopReportQuery(from, to, SearchBox.Text.Trim(), filters);
            var result = await _selectedSource.Source.LoadAsync(_selectedSource.Context, query, CancellationToken.None);
            _rows = result.Rows;
            ApplySearchAndCurrency();
            StatusText.Text = $"{_selectedSource.Source.Title}: consulta actualizada. Se cargaron {_rows.Count:N0} filas.";
        }
        catch (Exception exception)
        {
            _rows = [];
            ReportGrid.ItemsSource = null;
            ApplyVisibleResultCount();
            StatusText.Text = $"No se pudo cargar el reporte: {PosUserMessage.ForException(exception)}";
        }
        finally { SetBusy(false); }
    }

    private void ApplyColumnLayout(IReadOnlyList<ReportColumnLayout>? savedColumns)
    {
        if (_selectedSource is null) return;
        var definitions = _selectedSource.Source.Columns;
        var layout = savedColumns ?? definitions.Select((column, index) => new ReportColumnLayout(column.Id, column.VisibleByDefault, index)).ToArray();
        var projected = ReportColumnProjection.Apply(definitions, layout);
        var ordered = projected.Concat(definitions.Except(projected)).ToArray();
        ReportGrid.Columns.Clear();
        _columnIds.Clear();
        foreach (var definition in ordered)
        {
            var displayColumn = new DataGridTextColumn
            {
                Header = definition.Header,
                SortMemberPath = $"[{definition.Id}]",
                Binding = new Binding($"[{definition.Id}]") { StringFormat = definition.ValueType switch
                {
                    PosDesktopReportValueType.Decimal => "N2",
                    PosDesktopReportValueType.DateTime => "dd/MM/yyyy HH:mm",
                    _ => null
                } },
                Width = definition.ValueType == PosDesktopReportValueType.Text ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.SizeToHeader,
                Visibility = projected.Any(item => item.Id == definition.Id) ? Visibility.Visible : Visibility.Collapsed
            };
            ReportGrid.Columns.Add(displayColumn);
            _columnIds[displayColumn] = definition.Id;
        }
        ColumnPickerPanel.Children.Clear();
        foreach (var column in ordered)
        {
            var check = new CheckBox
            {
                Content = column.Header,
                Tag = column.Id,
                IsChecked = projected.Any(item => item.Id == column.Id),
                Margin = new Thickness(2, 4, 2, 4),
                ToolTip = $"Mostrar u ocultar {column.Header}"
            };
            System.Windows.Automation.AutomationProperties.SetName(check, $"Mostrar columna {column.Header}");
            check.Checked += ColumnVisibility_Changed;
            check.Unchecked += ColumnVisibility_Changed;
            ColumnPickerPanel.Children.Add(check);
        }
        UpdateColumnPickerButtonCaption();
    }

    private void ColumnPickerToggle_Changed(object sender, RoutedEventArgs e)
    {
        ColumnPickerPopup.IsOpen = ColumnPickerToggle.IsChecked == true;
    }

    private void ColumnVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id, IsChecked: bool visible }) return;
        var column = ReportGrid.Columns.FirstOrDefault(item => _columnIds.TryGetValue(item, out var columnId) && string.Equals(columnId, id, StringComparison.Ordinal));
        if (column is null) return;
        column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateColumnPickerButtonCaption();
        ApplyVisibleResultCount();
    }

    private void UpdateColumnPickerButtonCaption()
    {
        if (ColumnPickerToggle is null || ReportGrid is null) return;
        var visible = ReportGrid.Columns.Count(column => column.Visibility == Visibility.Visible);
        ColumnPickerToggle.Content = $"Columnas ({visible})";
    }

    private void UpdateColumnOrderInPicker()
    {
        foreach (var check in ColumnPickerPanel.Children.OfType<CheckBox>())
        {
            if (check.Tag is not string id) continue;
            var column = ReportGrid.Columns.FirstOrDefault(item => _columnIds.TryGetValue(item, out var columnId) && string.Equals(columnId, id, StringComparison.Ordinal));
            if (column is not null) check.ToolTip = $"Orden {column.DisplayIndex + 1}";
        }
    }

    private void ApplySearchAndCurrency()
    {
        if (!_uiReady || ReportGrid is null || SearchBox is null || CurrencyPicker is null) return;
        var search = SearchBox.Text.Trim();
        var currency = (CurrencyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var filtered = _rows.Where(row =>
        {
            if (currency is not null && currency != "Todas" &&
                row.TryGetValue("moneda", out var rowCurrency) && !string.Equals(rowCurrency?.ToString(), currency, StringComparison.OrdinalIgnoreCase)) return false;
            if (search.Length == 0) return true;
            return row.Values.Any(value => value?.ToString()?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true);
        }).ToArray();
        ReportGrid.ItemsSource = filtered;
        ApplyVisibleResultCount();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchAndCurrency();

    private void CurrencyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySearchAndCurrency();

    private void ApplyVisibleResultCount()
    {
        var visible = ReportGrid.Columns.Count(column => column.Visibility == Visibility.Visible);
        var count = ReportGrid.Items.Count;
        ResultCountText.Text = $"{count:N0} filas · {visible} columnas";
    }

    private async void SaveView_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSource is null || _context.ReportApiClient is null) return;
        var name = ViewNameBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "Escribe un nombre para guardar la vista."; ViewNameBox.Focus(); return; }
        var layout = ReportGrid.Columns.Select(column => new ReportColumnLayout(_columnIds[column],
            column.Visibility == Visibility.Visible, column.DisplayIndex)).OrderBy(item => item.DisplayOrder).ToArray();
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (FromDate.SelectedDate is { } from) filters["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (ToDate.SelectedDate is { } to) filters["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (CurrencyPicker.SelectedItem is ComboBoxItem { Content: string selectedCurrency } && selectedCurrency != "Todas") filters["currency"] = selectedCurrency;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) filters["search"] = SearchBox.Text.Trim();
        try
        {
            var saved = await _context.ReportApiClient.SaveReportViewAsync(_selectedSource.Source.Id,
                new SavedReportViewDefinition(name, layout, filters));
            StatusText.Text = $"Vista '{saved.Name}' guardada para tu usuario.";
            await LoadSavedViewsAsync();
            SavedViewsPicker.SelectedItem = _savedViews.FirstOrDefault(item => item.Id == saved.Id);
        }
        catch (Exception exception) { StatusText.Text = $"No se pudo guardar la vista: {PosUserMessage.ForException(exception)}"; }
    }

    private async void DeleteView_Click(object sender, RoutedEventArgs e)
    {
        if (_context.ReportApiClient is null || SavedViewsPicker.SelectedItem is not SavedReportViewInfo view) return;
        try
        {
            await _context.ReportApiClient.DeleteReportViewAsync(view.Id);
            StatusText.Text = $"Vista '{view.Name}' eliminada.";
            await LoadSavedViewsAsync();
        }
        catch (Exception exception) { StatusText.Text = $"No se pudo eliminar la vista: {PosUserMessage.ForException(exception)}"; }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Archivo CSV (*.csv)|*.csv", AddExtension = true, DefaultExt = ".csv", FileName = "reporte.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var columns = VisibleColumns(); var rows = VisibleRows();
        File.WriteAllText(dialog.FileName, ReportExport.ToCsv(columns, rows), new UTF8Encoding(true));
        StatusText.Text = $"CSV exportado con {rows.Count:N0} filas y {columns.Count} columnas.";
    }

    private void ExportXlsx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Libro de Excel (*.xlsx)|*.xlsx", AddExtension = true, DefaultExt = ".xlsx", FileName = "reporte.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        var columns = VisibleColumns(); var rows = VisibleRows();
        File.WriteAllBytes(dialog.FileName, ReportExport.ToXlsx(columns, rows));
        StatusText.Text = $"Excel exportado con {rows.Count:N0} filas y {columns.Count} columnas.";
    }

    private void PreviewSheet_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSource is null) return;
        var columns = VisibleColumns();
        if (columns.Count == 0)
        {
            StatusText.Text = "Activa al menos una columna para preparar la hoja del reporte.";
            return;
        }

        var filters = new List<string>();
        if (_selectedSource.Source.SupportsDateRange)
        {
            var from = FromDate.SelectedDate?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? "—";
            var to = ToDate.SelectedDate?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? "—";
            filters.Add($"Período: {from} al {to}");
        }
        if (CurrencyPicker.SelectedItem is ComboBoxItem { Content: string currency } && currency != "Todas")
            filters.Add($"Moneda: {currency}");
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) filters.Add($"Búsqueda: {SearchBox.Text.Trim()}");

        var preview = new ReportPreviewWindow(
            _selectedSource.Source.Title,
            string.Join("   ·   ", filters),
            columns,
            VisibleRows())
        {
            Owner = this
        };
        preview.ShowDialog();
    }

    private IReadOnlyList<PosDesktopReportColumn> VisibleColumns()
        => ReportGrid.Columns.Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex).Select(column => _selectedSource!.Source.Columns.Single(definition => definition.Id == _columnIds[column])).ToArray();

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> VisibleRows()
        => ReportGrid.Items.OfType<IReadOnlyDictionary<string, object?>>().ToArray();

    private static DateTime LocalDateStartUtc(DateTime date)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private void SetBusy(bool busy)
    {
        ReportPicker.IsEnabled = !busy;
        StatusText.Text = busy ? "Consultando el servidor…" : StatusText.Text;
    }
}

internal static class ReportExport
{
    public static string ToCsv(IReadOnlyList<PosDesktopReportColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var lines = new List<string> { string.Join(';', columns.Select(column => Escape(column.Header))) };
        lines.AddRange(rows.Select(row => string.Join(';', columns.Select(column => Escape(Format(row.GetValueOrDefault(column.Id)))))));
        return string.Join(Environment.NewLine, lines);
    }

    public static byte[] ToXlsx(IReadOnlyList<PosDesktopReportColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => XlsxReportWriter.Write(columns, rows);

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
        DateTimeOffset date => date.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.CurrentCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static string Escape(string value) => value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
        ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
