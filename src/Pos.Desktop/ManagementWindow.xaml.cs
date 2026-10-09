using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ManagementWindow : Window
{
    private readonly PosViewModel _viewModel;
    private IReadOnlyList<SalesReportRow> _salesRows = [];
    private IReadOnlyList<InventoryProductReportRow> _inventoryRows = [];
    private IReadOnlyList<SaleDetailRow> _saleDetailRows = [];
    private int _salesDetailPage = 1;
    private DateTime _salesDetailFromUtc;
    private DateTime _salesDetailToUtc;
    private int _salesDetailTotalCount;
    public ManagementWindow(PosViewModel viewModel, string initialSection = "Tasa")
    {
        InitializeComponent(); _viewModel = viewModel;
        ManagementTabs.SelectedIndex = initialSection switch
        {
            "Reportes" => 1,
            "Operadores" => 2,
            "Auditoría" => 3,
            _ => 0
        };
        FromDate.SelectedDate = DateTime.Today.AddDays(-30); ToDate.SelectedDate = DateTime.Today;
        Loaded += async (_, _) =>
        {
            await LoadRatesAsync(); await LoadOperatorsAsync(); await LoadAuditAsync();
            if (initialSection == "Reportes") await LoadDefaultReportsAsync();
        };
    }

    private async Task LoadDefaultReportsAsync()
    {
        var fromDate = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-30)).Date;
        var toDate = (ToDate.SelectedDate ?? DateTime.Today).Date;
        var zone = TimeZoneInfo.Local;
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromDate, DateTimeKind.Unspecified), zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Unspecified), zone);
        try
        {
            var sales = await _viewModel.GetSalesReportAsync(from, to);
            _salesRows = sales.Sales; ApplySalesReportFilter();
            _salesDetailPage = 1; _salesDetailFromUtc = from; _salesDetailToUtc = to;
            await LoadSalesDetailPageAsync();
            var cash = await _viewModel.GetCashSessionReportAsync(from, to, null);
            CashReportGrid.ItemsSource = cash.Sessions;
            CashReportSummary.Text = $"{cash.TotalCount} turnos · mostrando {cash.Sessions.Count}. USD y VES se muestran por separado.";
            ReportStatus.Text = $"Reportes cargados automáticamente para {fromDate:dd/MM/yyyy} al {toDate:dd/MM/yyyy}.";
        }
        catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    private async Task LoadRatesAsync() { try { RatesGrid.ItemsSource = await _viewModel.GetExchangeRatesAsync(); RateStatus.Text = "La tasa USD/VES vigente se aplica automáticamente a productos con moneda distinta a la caja."; } catch (Exception ex) { RateStatus.Text = PosUserMessage.ForException(ex); } }
    private async Task LoadOperatorsAsync() { try { OperatorsGrid.ItemsSource = await _viewModel.GetOperatorsAsync(); OperatorStatus.Text = "Los cambios de rol se aplican a las sesiones activas."; } catch (Exception ex) { OperatorStatus.Text = PosUserMessage.ForException(ex); } }
    private async Task LoadAuditAsync() { try { AuditGrid.ItemsSource = await _viewModel.GetAuditAsync(); AuditStatus.Text = "Últimos 200 eventos. Solo roles autorizados pueden consultar."; } catch (Exception ex) { AuditStatus.Text = PosUserMessage.ForException(ex); } }

    private async void SetRate_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(RateBox.Text, out var rate) || rate <= 0m)
        { RateStatus.Text = "La tasa debe ser mayor que cero."; return; }
        try { await _viewModel.SetExchangeRateAsync(Selected(FromCurrencyBox), Selected(ToCurrencyBox), rate, DateTime.UtcNow, RateSourceBox.Text.Trim()); RateStatus.Text = "Tasa guardada."; await LoadRatesAsync(); }
        catch (Exception ex) { RateStatus.Text = PosUserMessage.ForException(ex); }
    }

    private void TodayRange_Click(object sender, RoutedEventArgs e) => SetReportRange(DateTime.Today, DateTime.Today);
    private void WeekRange_Click(object sender, RoutedEventArgs e) => SetReportRange(DateTime.Today.AddDays(-6), DateTime.Today);
    private void MonthRange_Click(object sender, RoutedEventArgs e) => SetReportRange(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);

    private void SetReportRange(DateTime from, DateTime to)
    {
        FromDate.SelectedDate = from;
        ToDate.SelectedDate = to;
    }

    private async void RunSelectedReport_Click(object sender, RoutedEventArgs e)
    {
            if (ReportTabs.SelectedIndex == 3)
            {
                await LoadInventoryReportAsync();
                return;
            }

        var fromDate = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-6)).Date;
        var toDate = (ToDate.SelectedDate ?? DateTime.Today).Date;
        if (toDate < fromDate) { ReportStatus.Text = "La fecha final debe ser igual o posterior a la fecha inicial."; return; }
        if ((toDate - fromDate).TotalDays >= 92) { ReportStatus.Text = "El período máximo por consulta es de 92 días."; return; }
        var localZone = TimeZoneInfo.Local;
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromDate, DateTimeKind.Unspecified), localZone);
        var to = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Unspecified), localZone);
        try
        {
            if (ReportTabs.SelectedIndex == 1)
            {
                _salesDetailPage = 1; _salesDetailFromUtc = from; _salesDetailToUtc = to;
                await LoadSalesDetailPageAsync();
                ReportStatus.Text = $"Detalle de ventas: {fromDate:dd/MM/yyyy} al {toDate:dd/MM/yyyy}. Selecciona una fila para consultar artículos y pagos.";
            }
            else if (ReportTabs.SelectedIndex == 2)
            {
                var status = (CashStatusBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
                var report = await _viewModel.GetCashSessionReportAsync(from, to, status);
                CashReportGrid.ItemsSource = report.Sessions;
                CashReportSummary.Text = $"{report.TotalCount} turnos · mostrando {report.Sessions.Count}. Los arqueos USD y VES se presentan por separado.";
                ReportStatus.Text = $"Cajas abiertas durante el período {fromDate:dd/MM/yyyy} al {toDate:dd/MM/yyyy}.";
            }
            else
            {
                var report = await _viewModel.GetSalesReportAsync(from, to);
                _salesRows = report.Sales;
                ApplySalesReportFilter();
                ReportStatus.Text = $"Consulta de ventas: {fromDate:dd/MM/yyyy} al {toDate:dd/MM/yyyy}. Los totales se muestran por moneda.";
            }
        }
        catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private async Task LoadInventoryReportAsync()
    {
        try
        {
            var products = await _viewModel.GetInventoryAsync();
            _inventoryRows = products.Select(x => new InventoryProductReportRow(x.Sku, x.Name, x.Quantity, x.UnitPrice, x.Currency, x.Quantity * x.UnitPrice)).ToArray();
            ApplyInventoryReportFilter();
            ReportStatus.Text = "Existencias actuales. El valor es una estimación al precio de venta, no representa costo ni utilidad.";
        }
        catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private void SalesDetail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalesDetailGrid.SelectedItem is not SaleDetailRow sale) return;
        SaleLinesGrid.ItemsSource = sale.Lines;
        var payments = string.Join(" · ", sale.Payments.Select(x => $"{x.Method}: {x.Currency} {PosNumberFormat.FormatAmount(x.Amount)}{(x.AppliedExchangeRate.HasValue ? $" (tasa {PosNumberFormat.FormatAmount(x.AppliedExchangeRate.Value)})" : string.Empty)}"));
        SaleDetailTitle.Text = $"Venta {sale.Id.ToString("N")[..8]} · {sale.CustomerName} · Pagos: {payments}";
    }

    private async Task LoadSalesDetailPageAsync()
    {
        var detail = await _viewModel.GetSalesDetailReportAsync(_salesDetailFromUtc, _salesDetailToUtc, CurrencyFilter, _salesDetailPage);
        _saleDetailRows = detail.Sales; _salesDetailTotalCount = detail.TotalCount;
        ApplySalesDetailFilter(); SaleLinesGrid.ItemsSource = null;
        var pages = Math.Max(1, (detail.TotalCount + detail.PageSize - 1) / detail.PageSize);
        SalesDetailSummary.Text = $"{detail.TotalCount} ventas · página de {detail.Sales.Count} registros · USD y VES se mantienen separados.";
        SalesDetailPageLabel.Text = $"Página {detail.Page} de {pages}";
        SalesDetailPrevious.IsEnabled = detail.Page > 1; SalesDetailNext.IsEnabled = detail.Page < pages;
    }

    private async void SalesDetailPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_salesDetailPage <= 1) return;
        _salesDetailPage--; try { await LoadSalesDetailPageAsync(); } catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private async void SalesDetailNext_Click(object sender, RoutedEventArgs e)
    {
        if (_salesDetailPage * 250 >= _salesDetailTotalCount) return;
        _salesDetailPage++; try { await LoadSalesDetailPageAsync(); } catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private void SalesDetailSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplySalesDetailFilter();

    private void ApplySalesDetailFilter()
    {
        if (SalesDetailGrid is null) return;
        var term = SalesDetailSearchBox?.Text.Trim() ?? string.Empty;
        var rows = _saleDetailRows.Where(s => term.Length == 0 || s.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            s.OperatorName.Contains(term, StringComparison.OrdinalIgnoreCase) || s.Id.ToString("N").Contains(term, StringComparison.OrdinalIgnoreCase) ||
            s.Lines.Any(line => line.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) || line.Description.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        SalesDetailGrid.ItemsSource = rows;
    }

    private string CurrencyFilter => (ReportCurrencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() is { } value && value != "Todas" ? value : string.Empty;

    private void ReportCurrency_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalesReportGrid is null || InventoryReportGrid is null) return;
        ApplySalesReportFilter();
        ApplyInventoryReportFilter();
        if (ReportTabs?.SelectedIndex == 1 && SalesDetailGrid?.ItemsSource is not null)
        {
            _salesDetailPage = 1;
            _ = ReloadSalesDetailForCurrencyAsync();
        }
    }

    private async Task ReloadSalesDetailForCurrencyAsync()
    {
        try { await LoadSalesDetailPageAsync(); }
        catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private void InventorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (InventoryReportGrid is not null) ApplyInventoryReportFilter();
    }

    private void ApplySalesReportFilter()
    {
        if (SalesReportGrid is null) return;
        var currency = CurrencyFilter;
        var rows = _salesRows.Where(x => currency.Length == 0 || x.Currency == currency).ToArray();
        SalesReportGrid.ItemsSource = rows;
        var count = rows.Sum(x => x.Count);
        var totals = rows.GroupBy(x => x.Currency).OrderBy(x => x.Key)
            .Select(x => $"{x.Key} {PosNumberFormat.FormatAmount(x.Sum(y => y.Total))}");
        SalesSummary.Text = rows.Length == 0
            ? "No hay ventas registradas en el período seleccionado."
            : $"{count} ventas · {string.Join("   |   ", totals)}";
    }

    private void ApplyInventoryReportFilter()
    {
        if (InventoryReportGrid is null) return;
        var currency = CurrencyFilter;
        var search = InventorySearchBox.Text.Trim();
        var rows = _inventoryRows.Where(x => (currency.Length == 0 || x.Currency == currency) &&
            (search.Length == 0 || x.Sku.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Name).ToArray();
        InventoryReportGrid.ItemsSource = rows;
        var totals = rows.GroupBy(x => x.Currency).OrderBy(x => x.Key)
            .Select(x => $"{x.Key}: {PosNumberFormat.FormatQuantity(x.Sum(y => y.Quantity))} unidades · valor {PosNumberFormat.FormatAmount(x.Sum(y => y.RetailValue))}");
        InventorySummary.Text = rows.Length == 0 ? "No hay productos que coincidan con estos filtros." : $"{rows.Length} productos · {string.Join("   |   ", totals)}";
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (ReportTabs.SelectedIndex == 3 ? InventoryReportGrid.ItemsSource is null : ReportTabs.SelectedIndex == 1 ? SalesDetailGrid.ItemsSource is null : ReportTabs.SelectedIndex == 2 ? CashReportGrid.ItemsSource is null : SalesReportGrid.ItemsSource is null)
        {
            ReportStatus.Text = "Primero consulta el reporte que deseas exportar.";
            return;
        }
        var saveDialog = new SaveFileDialog { Filter = "Archivo CSV (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = ReportTabs.SelectedIndex == 3 ? "existencias.csv" : ReportTabs.SelectedIndex == 2 ? "cajas.csv" : ReportTabs.SelectedIndex == 1 ? "ventas-detalladas.csv" : "ventas.csv" };
        if (saveDialog.ShowDialog(this) != true) return;
        var lines = ReportTabs.SelectedIndex == 3 ? ExportInventoryRows()
            : ReportTabs.SelectedIndex == 1 ? ExportSaleDetailRows()
            : ReportTabs.SelectedIndex == 2 ? ExportCashRows()
            : ExportSalesRows();
        if (lines.Count == 0) { ReportStatus.Text = "No hay resultados para exportar. Consulta un reporte primero."; return; }
        try
        {
            File.WriteAllLines(saveDialog.FileName, lines, new UTF8Encoding(true));
            ReportStatus.Text = $"Reporte exportado: {saveDialog.FileName}";
        }
        catch (Exception ex) { ReportStatus.Text = PosUserMessage.ForException(ex); }
    }

    private List<string> ExportSalesRows()
    {
        var rows = SalesReportGrid.ItemsSource as IEnumerable<SalesReportRow> ?? [];
        return ["Fecha;Moneda;Cantidad de ventas;Total", .. rows.Select(x => $"{x.Day:dd/MM/yyyy};{Csv(x.Currency)};{x.Count};{PosNumberFormat.FormatAmount(x.Total)}")];
    }

    private List<string> ExportInventoryRows()
    {
        var rows = InventoryReportGrid.ItemsSource as IEnumerable<InventoryProductReportRow> ?? [];
        return ["Código;Producto;Existencia;Precio unitario;Moneda;Valor al precio de venta", .. rows.Select(x => $"{Csv(x.Sku)};{Csv(x.Name)};{PosNumberFormat.FormatQuantity(x.Quantity)};{PosNumberFormat.FormatAmount(x.UnitPrice)};{Csv(x.Currency)};{PosNumberFormat.FormatAmount(x.RetailValue)}")];
    }

    private List<string> ExportSaleDetailRows()
    {
        var rows = SalesDetailGrid.ItemsSource as IEnumerable<SaleDetailRow> ?? [];
        return ["Fecha;Venta;Cliente;Responsable;Código;Producto;Cantidad;Precio;Importe;Moneda;Pagos",
            .. rows.SelectMany(s => s.Lines.Select(line => $"{s.CreatedAtUtc:dd/MM/yyyy HH:mm:ss};{s.Id};{Csv(s.CustomerName)};{Csv(s.OperatorName)};{Csv(line.Sku)};{Csv(line.Description)};{PosNumberFormat.FormatQuantity(line.Quantity)};{PosNumberFormat.FormatAmount(line.UnitPrice)};{PosNumberFormat.FormatAmount(line.LineTotal)};{s.Currency};{Csv(string.Join(" | ", s.Payments.Select(payment => $"{payment.Method}: {payment.Currency} {PosNumberFormat.FormatAmount(payment.Amount)}")))}"))];
    }

    private List<string> ExportCashRows()
    {
        var rows = CashReportGrid.ItemsSource as IEnumerable<CashSessionReportRow> ?? [];
        return ["Apertura;Cierre;Responsable;Estado;USD inicial;USD esperado;USD contado;Diferencia USD;VES inicial;VES esperado;VES contado;Diferencia VES",
            .. rows.Select(x => $"{x.OpenedAtUtc:dd/MM/yyyy HH:mm};{x.ClosedAtUtc:dd/MM/yyyy HH:mm};{Csv(x.OperatorName)};{x.Status};{PosNumberFormat.FormatAmount(x.OpeningUsd)};{x.ExpectedUsd:N2};{x.CountedUsd:N2};{x.VarianceUsd:N2};{PosNumberFormat.FormatAmount(x.OpeningVes)};{x.ExpectedVes:N2};{x.CountedVes:N2};{x.VarianceVes:N2}")];
    }

    private static string Csv(string value) => value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
        ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private async void CreateOperator_Click(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.CreateOperatorAsync(OperatorNameBox.Text.Trim(), OperatorPinBox.Password, Selected(OperatorRoleBox)); OperatorPinBox.Clear(); OperatorNameBox.Clear(); await LoadOperatorsAsync(); }
        catch (Exception ex) { OperatorStatus.Text = PosUserMessage.ForException(ex); }
    }

    private async void ChangeRole_Click(object sender, RoutedEventArgs e)
    {
        if (OperatorsGrid.SelectedItem is not OperatorInfo item) { OperatorStatus.Text = "Selecciona un operador."; return; }
        try { await _viewModel.SetOperatorRoleAsync(item.Id, Selected(OperatorRoleBox)); await LoadOperatorsAsync(); }
        catch (Exception ex) { OperatorStatus.Text = PosUserMessage.ForException(ex); }
    }

    private async void ToggleOperator_Click(object sender, RoutedEventArgs e)
    {
        if (OperatorsGrid.SelectedItem is not OperatorInfo item) { OperatorStatus.Text = "Selecciona un operador."; return; }
        var answer = MessageBox.Show(this, $"¿{(item.Active ? "desactivar" : "activar")} a {item.Username}?", "Estado del operador", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try { await _viewModel.SetOperatorActiveAsync(item.Id, !item.Active); await LoadOperatorsAsync(); }
        catch (Exception ex) { OperatorStatus.Text = PosUserMessage.ForException(ex); }
    }
}
