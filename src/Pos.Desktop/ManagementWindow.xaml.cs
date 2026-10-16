using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ManagementWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly Func<CancellationToken, Task>? _restartLocalServiceAsync;
    private IReadOnlyList<SalesReportRow> _salesRows = [];
    private IReadOnlyList<InventoryProductReportRow> _inventoryRows = [];
    private IReadOnlyList<ProductSalesReportRow> _productSalesRows = [];
    private int _salesDetailPage = 1;
    private DateTime _salesDetailFromUtc;
    private DateTime _salesDetailToUtc;
    private int _salesDetailTotalCount;
    private string[] _modulePackagePaths = [];
    private ModuleInstallationPlanClientResult? _validatedModulePlan;
    private Guid? _licenseInstallationId;
    private string _licenseState = string.Empty;
    private bool _managementReady;
    public ManagementWindow(PosViewModel viewModel, string initialSection = "Tasa",
        Func<CancellationToken, Task>? restartLocalServiceAsync = null)
    {
        InitializeComponent(); _viewModel = viewModel; _restartLocalServiceAsync = restartLocalServiceAsync;
        RestartModuleHostButton.IsEnabled = _restartLocalServiceAsync is not null;
        if (_restartLocalServiceAsync is null)
            RestartModuleHostButton.ToolTip = "Esta terminal está conectada a otro equipo. Reinicia el POS desde la computadora servidor.";
        ManagementTabs.SelectedIndex = initialSection switch
        {
            "Reportes" => 1,
            "Operadores" => 2,
            "Auditoría" => 3,
            "Módulos" => 4,
            "Licencia" or "Licensing" => 5,
            _ => 0
        };
        FromDate.SelectedDate = DateTime.Today.AddDays(-30); ToDate.SelectedDate = DateTime.Today;
        Loaded += async (_, _) =>
        {
            _managementReady = true;
            UpdateReportFilterVisibility();
            await LoadRatesAsync(); await LoadOperatorsAsync(); await LoadAuditAsync();
            if (initialSection == "Reportes") await LoadDefaultReportsAsync();
            if (initialSection == "Módulos") await LoadInstalledModulesAsync();
            if (initialSection is "Licencia" or "Licensing") await LoadLicenseStatusAsync();
        };
    }

    private async void ManagementTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_managementReady && ReferenceEquals(sender, ManagementTabs) && ReferenceEquals(ManagementTabs.SelectedItem, LicenseTab))
            await LoadLicenseStatusAsync();
    }

    private async Task LoadLicenseStatusAsync()
    {
        try
        {
            var license = await _viewModel.GetLicenseStatusAsync();
            _licenseState = license.State;
            LicenseStateText.Text = license.StateLabel;
            LicenseCustomerText.Text = $"Comercio: {license.CustomerName ?? "—"}";
            LicensePlanText.Text = $"Plan: {license.Plan ?? "—"}{(license.LicenseId is null ? string.Empty : $" · licencia {license.LicenseId}")}";
            LicenseExpiryText.Text = license.ExpiresAtUtc is { } expiry
                ? $"Vence: {expiry.ToLocalTime():dd/MM/yyyy HH:mm} · hora local"
                : "Vencimiento: —";
            LicenseTerminalLimitText.Text = license.MaxTerminals is { } maximum
                ? $"Terminales permitidas: {maximum}"
                : "Terminales permitidas: —";
            LicenseEntitlementsText.Text = license.Entitlements.Count == 0
                ? "Módulos autorizados: ninguno"
                : $"Módulos autorizados: {string.Join(", ", license.Entitlements)}";
            _licenseInstallationId = license.InstallationId;
            LicenseInstallationText.Text = $"ID de instalación: {license.InstallationId:D}";
            CopyLicenseInstallationIdButton.IsEnabled = true;
            CreateRecoveryRequestButton.IsEnabled = license.State is "Locked" or "Suspended";
            CreateRecoveryRequestButton.ToolTip = CreateRecoveryRequestButton.IsEnabled
                ? "Genera una solicitud para autorizar desde el VPS un desbloqueo temporal de esta instalación."
                : "Disponible cuando la licencia esté bloqueada o suspendida.";
            LicenseStatus.Text = license.Diagnostic ?? (license.State is "Active" or "Grace"
                ? "La licencia está almacenada y validada localmente."
                : "Importa una licencia emitida para este ID de instalación.");
            LicenseStateText.Foreground = license.State switch
            {
                "Active" => System.Windows.Media.Brushes.DarkGreen,
                "Grace" => System.Windows.Media.Brushes.DarkOrange,
                "Recovery" => System.Windows.Media.Brushes.DarkBlue,
                "Locked" or "Suspended" or "Invalid" or "ClockRollback" => System.Windows.Media.Brushes.DarkRed,
                _ => System.Windows.Media.Brushes.DarkSlateGray
            };
        }
        catch (Exception exception)
        {
            LicenseStatus.Text = PosUserMessage.ForException(exception);
        }
    }

    private async void RefreshLicense_Click(object sender, RoutedEventArgs e)
        => await LoadLicenseStatusAsync();

    private async void CreateRecoveryRequest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CreateRecoveryRequestButton.IsEnabled = false;
            LicenseStatus.Text = "Preparando solicitud de desbloqueo para esta instalación…";
            var document = await _viewModel.CreateLicenseRecoveryRequestAsync();
            var installationSuffix = _licenseInstallationId.HasValue ? _licenseInstallationId.Value.ToString("N")[..8] : "pos";
            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".posreq",
                FileName = $"solicitud-{installationSuffix}.posreq",
                Filter = "Solicitud de desbloqueo POS (*.posreq)|*.posreq",
                Title = "Guardar solicitud para el VPS"
            };
            if (dialog.ShowDialog(this) != true)
            {
                LicenseStatus.Text = "La solicitud se creó localmente, pero no se guardó un archivo. Puedes volver a solicitarla cuando quieras.";
                return;
            }
            await File.WriteAllBytesAsync(dialog.FileName, document);
            LicenseStatus.Text = "Solicitud guardada. Llévala al VPS con USB o transferencia segura y selecciona allí ‘Crear desbloqueo offline’.";
        }
        catch (Exception exception)
        {
            LicenseStatus.Text = PosUserMessage.ForException(exception);
        }
        finally { CreateRecoveryRequestButton.IsEnabled = _licenseState is "Locked" or "Suspended"; }
    }

    private async void ApplyRecoveryGrant_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Desbloqueo temporal POS (*.posunlock)|*.posunlock",
            Title = "Seleccionar desbloqueo firmado por el VPS"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length is <= 0 or > 32 * 1024)
            {
                LicenseStatus.Text = "El archivo de desbloqueo está vacío o supera 32 KiB.";
                return;
            }
            ApplyRecoveryGrantButton.IsEnabled = false;
            LicenseStatus.Text = "Verificando firma y solicitud de esta instalación…";
            var status = await _viewModel.ApplyLicenseRecoveryGrantAsync(await File.ReadAllBytesAsync(dialog.FileName));
            await LoadLicenseStatusAsync();
            LicenseStatus.Text = status.Diagnostic ?? "Desbloqueo temporal aplicado y verificado.";
        }
        catch (Exception exception)
        {
            LicenseStatus.Text = PosUserMessage.ForException(exception);
        }
        finally { ApplyRecoveryGrantButton.IsEnabled = true; }
    }

    private void CopyLicenseInstallationId_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseInstallationId is not { } installationId) return;

        try
        {
            Clipboard.SetText(installationId.ToString("D"));
            LicenseStatus.Text = "ID de instalación copiado al portapapeles.";
        }
        catch (Exception exception)
        {
            LicenseStatus.Text = PosUserMessage.ForException(exception);
        }
    }

    private async void ImportLicense_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecciona la licencia firmada del POS",
            Filter = "Licencia BalanzaPro (*.poslic)|*.poslic|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length is <= 0 or > 64 * 1024)
            {
                LicenseStatus.Text = "El archivo está vacío o supera el límite de 64 KiB.";
                return;
            }

            ImportLicenseButton.IsEnabled = false;
            LicenseStatus.Text = "Verificando firma e instalación en el servidor…";
            var document = await File.ReadAllBytesAsync(dialog.FileName);
            var status = await _viewModel.ImportLicenseAsync(document);
            await LoadLicenseStatusAsync();
            LicenseStatus.Text = $"Licencia {status.LicenseId ?? string.Empty} importada y verificada en el servidor.";
        }
        catch (Exception exception)
        {
            LicenseStatus.Text = PosUserMessage.ForException(exception);
        }
        finally
        {
            ImportLicenseButton.IsEnabled = true;
        }
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

    private void ReportTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ReportTabs)) UpdateReportFilterVisibility();
    }

    private void UpdateReportFilterVisibility()
    {
        var index = ReportTabs?.SelectedIndex ?? 0;
        CurrencyFiltersPanel.Visibility = index is 0 or 1 or 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
        SalesDetailFiltersPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        CashFiltersPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        InventoryFiltersPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ProductSalesFiltersPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
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
            else if (ReportTabs.SelectedIndex == 4)
            {
                await LoadProductSalesReportAsync(from, to);
                ReportStatus.Text = $"Ventas por producto del {fromDate:dd/MM/yyyy} al {toDate:dd/MM/yyyy}. Las devoluciones se restan de las ventas asociadas.";
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

    private async Task LoadProductSalesReportAsync(DateTime fromUtc, DateTime toUtc)
    {
        var report = await _viewModel.GetProductSalesReportAsync(fromUtc, toUtc, "Todas");
        _productSalesRows = report.Products;
        ApplyProductSalesFilter();
    }

    private void CashReport_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, CashReportGrid)) return;
        if (CashReportGrid.SelectedItem is not CashSessionReportRow session)
        {
            CashSessionDetailsGrid.ItemsSource = null;
            CashMovementsGrid.ItemsSource = null;
            return;
        }
        CashSessionDetailsGrid.ItemsSource = session.Payments;
        CashMovementsGrid.ItemsSource = session.Movements;
    }

    private void ProductSalesSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyProductSalesFilter();

    private void ApplyProductSalesFilter()
    {
        if (ProductSalesGrid is null) return;
        var currency = CurrencyFilter;
        var search = ProductSalesSearchBox?.Text.Trim() ?? string.Empty;
        var rows = _productSalesRows.Where(row =>
                (currency.Length == 0 || row.Currency == currency) &&
                (search.Length == 0 || row.Sku.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(row => row.NetAmount).ThenBy(row => row.Name).ToArray();
        ProductSalesGrid.ItemsSource = rows;
        var totals = rows.GroupBy(row => row.Currency).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}: {PosNumberFormat.FormatQuantity(group.Sum(row => row.NetQuantity))} unidades netas · neto {PosNumberFormat.FormatAmount(group.Sum(row => row.NetAmount))}");
        ProductSalesSummary.Text = rows.Length == 0 ? "No hay productos vendidos que coincidan con estos filtros." : $"{rows.Length} productos · {string.Join("   |   ", totals)}";
    }

    private void SalesDetail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalesDetailGrid.SelectedItem is not SaleDetailRow sale)
        { SaleReturnButton.IsEnabled = false; return; }
        SaleLinesGrid.ItemsSource = sale.Lines;
        SaleReturnButton.IsEnabled = sale.Status != "Returned" && sale.Lines.Any(x => x.ReturnableQuantity > 0m);
        var payments = string.Join(" · ", sale.Payments.Select(x => x.DescribeForSaleCurrency(sale.Currency)));
        var rounding = string.IsNullOrEmpty(sale.RoundingAdjustmentLabel) ? string.Empty : $" · {sale.RoundingAdjustmentLabel}";
        SaleDetailTitle.Text = $"Venta {sale.SaleNumber} · {sale.CustomerName} · Estado: {sale.StatusLabel} · Moneda de venta: {sale.Currency} · Total: {sale.Currency} {PosNumberFormat.FormatAmount(sale.Total)}{rounding} · Devuelto: {sale.Currency} {PosNumberFormat.FormatAmount(sale.ReturnedAmount)} · Pagos recibidos: {payments}";
    }

    private async void SaleReturn_Click(object sender, RoutedEventArgs e)
    {
        if (SalesDetailGrid.SelectedItem is not SaleDetailRow sale) return;
        var window = new SaleReturnWindow(_viewModel, sale) { Owner = this };
        if (window.ShowDialog() != true) return;
        try
        {
            await LoadSalesDetailPageAsync();
            ReportStatus.Text = "Devolución registrada. Se actualizaron los reportes y las cantidades pendientes.";
        }
        catch (Exception exception) { ReportStatus.Text = PosUserMessage.ForException(exception); }
    }

    private async Task LoadSalesDetailPageAsync()
    {
        var paymentMethod = Selected(SalesPaymentMethodBox);
        if (paymentMethod == "Todos") paymentMethod = string.Empty;
        var status = Selected(SalesStatusBox);
        var detail = await _viewModel.GetSalesDetailReportAsync(_salesDetailFromUtc, _salesDetailToUtc, CurrencyFilter, _salesDetailPage,
            paymentMethod: paymentMethod, operatorName: SalesOperatorBox.Text.Trim(), status: status, query: SalesDetailSearchBox.Text.Trim());
        _salesDetailTotalCount = detail.TotalCount;
        SalesDetailGrid.ItemsSource = detail.Sales; SaleLinesGrid.ItemsSource = null;
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

    private string CurrencyFilter => (ReportCurrencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() is { } value && value != "Todas" ? value : string.Empty;

    private void ReportCurrency_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SalesReportGrid is null || InventoryReportGrid is null) return;
        ApplySalesReportFilter();
        ApplyInventoryReportFilter();
        if (ProductSalesGrid?.ItemsSource is not null) ApplyProductSalesFilter();
        if (ReportTabs?.SelectedIndex == 1 && SalesDetailGrid?.ItemsSource is not null)
            ReportStatus.Text = "Los filtros cambiaron. Pulsa Consultar reporte para actualizar las ventas.";
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
        if (ReportTabs.SelectedIndex == 3 ? InventoryReportGrid.ItemsSource is null : ReportTabs.SelectedIndex == 4 ? ProductSalesGrid.ItemsSource is null : ReportTabs.SelectedIndex == 1 ? SalesDetailGrid.ItemsSource is null : ReportTabs.SelectedIndex == 2 ? CashReportGrid.ItemsSource is null : SalesReportGrid.ItemsSource is null)
        {
            ReportStatus.Text = "Primero consulta el reporte que deseas exportar.";
            return;
        }
        var saveDialog = new SaveFileDialog { Filter = "Archivo CSV (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = ReportTabs.SelectedIndex == 3 ? "existencias.csv" : ReportTabs.SelectedIndex == 4 ? "ventas-por-producto.csv" : ReportTabs.SelectedIndex == 2 ? "cajas.csv" : ReportTabs.SelectedIndex == 1 ? "ventas-detalladas.csv" : "ventas.csv" };
        if (saveDialog.ShowDialog(this) != true) return;
        var lines = ReportTabs.SelectedIndex == 3 ? ExportInventoryRows()
            : ReportTabs.SelectedIndex == 4 ? ExportProductSalesRows()
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

    private List<string> ExportProductSalesRows()
    {
        var rows = ProductSalesGrid.ItemsSource as IEnumerable<ProductSalesReportRow> ?? [];
        return ["Código;Producto;Moneda;Cantidad vendida;Cantidad devuelta;Cantidad neta;Importe bruto;Importe devuelto;Importe neto",
            .. rows.Select(x => $"{Csv(x.Sku)};{Csv(x.Name)};{Csv(x.Currency)};{PosNumberFormat.FormatQuantity(x.SoldQuantity)};{PosNumberFormat.FormatQuantity(x.ReturnedQuantity)};{PosNumberFormat.FormatQuantity(x.NetQuantity)};{PosNumberFormat.FormatAmount(x.GrossAmount)};{PosNumberFormat.FormatAmount(x.ReturnedAmount)};{PosNumberFormat.FormatAmount(x.NetAmount)}")];
    }

    private List<string> ExportSaleDetailRows()
    {
        var rows = SalesDetailGrid.ItemsSource as IEnumerable<SaleDetailRow> ?? [];
        return ["Fecha;Venta;Cliente;Responsable;Código;Producto;Cantidad;Precio;Importe;Moneda de venta;Ajuste por redondeo;Pagos recibidos (moneda e importe real; equivalente según tasa registrada)",
            .. rows.SelectMany(s => s.Lines.Select(line => $"{s.CreatedAtUtc:dd/MM/yyyy HH:mm:ss};{s.SaleNumber};{Csv(s.CustomerName)};{Csv(s.OperatorName)};{Csv(line.Sku)};{Csv(line.Description)};{PosNumberFormat.FormatQuantity(line.Quantity)};{PosNumberFormat.FormatAmount(line.UnitPrice)};{PosNumberFormat.FormatAmount(line.LineTotal)};{s.Currency};{Csv(s.RoundingAdjustmentLabel)};{Csv(string.Join(" | ", s.Payments.Select(payment => payment.DescribeForSaleCurrency(s.Currency))))}"))];
    }

    private List<string> ExportCashRows()
    {
        var rows = CashReportGrid.ItemsSource as IEnumerable<CashSessionReportRow> ?? [];
        var lines = new List<string>
        {
            "Apertura;Cierre;Responsable;Estado;USD inicial;USD esperado;USD contado;Diferencia USD;VES inicial;VES esperado;VES contado;Diferencia VES;Tipo de detalle;Método o movimiento;Moneda detalle;Importe detalle;Motivo"
        };
        foreach (var row in rows)
        {
            var summary = $"{row.OpenedAtUtc:dd/MM/yyyy HH:mm};{row.ClosedAtUtc:dd/MM/yyyy HH:mm};{Csv(row.OperatorName)};{Csv(row.StatusLabel)};{PosNumberFormat.FormatAmount(row.OpeningUsd)};{PosNumberFormat.FormatAmount(row.ExpectedUsd ?? 0m)};{PosNumberFormat.FormatAmount(row.CountedUsd ?? 0m)};{PosNumberFormat.FormatAmount(row.VarianceUsd ?? 0m)};{PosNumberFormat.FormatAmount(row.OpeningVes)};{PosNumberFormat.FormatAmount(row.ExpectedVes ?? 0m)};{PosNumberFormat.FormatAmount(row.CountedVes ?? 0m)};{PosNumberFormat.FormatAmount(row.VarianceVes ?? 0m)}";
            lines.Add(summary + ";Arqueo;;;;");
            lines.AddRange(row.Payments.Select(payment => $";;;;;;;;;;;;{Csv(payment.Operation)};{Csv(payment.Method)};{Csv(payment.Currency)};{PosNumberFormat.FormatAmount(payment.Amount)};"));
            lines.AddRange(row.Movements.Select(movement => $";;;;;;;;;;;;Movimiento;{Csv(movement.DirectionLabel)};{Csv(movement.Currency)};{PosNumberFormat.FormatAmount(movement.Amount)};{Csv(movement.Reason)}"));
        }
        return lines;
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

    private async void ManagePermissions_Click(object sender, RoutedEventArgs e)
    {
        if (OperatorsGrid.SelectedItem is not OperatorInfo item) { OperatorStatus.Text = "Selecciona un operador."; return; }
        if (item.Role == "Administrator") { OperatorStatus.Text = "El administrador conserva todos los permisos."; return; }
        var window = new OperatorPermissionsWindow(_viewModel, item) { Owner = this };
        if (window.ShowDialog() == true) await LoadOperatorsAsync();
    }

    private void ChooseModulePackages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar módulos oficiales", Filter = "Paquetes de módulo (*.zip)|*.zip", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _modulePackagePaths = dialog.FileNames;
        _validatedModulePlan = null;
        ModulePlanGrid.ItemsSource = null;
        InstallModuleButton.IsEnabled = false;
        ModulePackageFilesLabel.Text = string.Join(" · ", _modulePackagePaths.Select(Path.GetFileName));
        try
        {
            var packages = _modulePackagePaths.Select(ModulePackageDescriptorReader.Read).ToArray();
            if (packages.Length == 1)
            {
                ModuleIdBox.Text = packages[0].Id;
                ModulePackageIdentityLabel.Text = $"Módulo: {packages[0].DisplayName} · ID interno: {packages[0].Id} · versión {packages[0].Version}. El ID se completó desde el manifiesto del ZIP.";
            }
            else
            {
                ModulePackageIdentityLabel.Text = "Paquetes detectados: " + string.Join("; ", packages.Select(package => $"{package.DisplayName} (ID: {package.Id}, versión {package.Version})")) + ". Escribe el ID del módulo principal que quieres instalar.";
                ModuleIdBox.Clear();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            ModuleIdBox.Clear();
            ModulePackageIdentityLabel.Text = PosUserMessage.ForException(exception);
        }
        ModuleStatus.Text = "Valida el lote para revisar firmas, compatibilidad y dependencias antes de instalar.";
    }

    private async void ValidateModule_Click(object sender, RoutedEventArgs e)
    {
        InstallModuleButton.IsEnabled = false;
        ValidateModuleButton.IsEnabled = false;
        try
        {
            if (_modulePackagePaths.Length == 0) { ModuleStatus.Text = "Selecciona uno o más paquetes ZIP."; return; }
            if (string.IsNullOrWhiteSpace(ModuleIdBox.Text)) { ModuleStatus.Text = "Selecciona un ZIP del módulo para completar su ID, o escribe el ID interno que aparece junto al nombre del paquete."; return; }
            var plan = await _viewModel.PlanModuleInstallationAsync(ModuleIdBox.Text.Trim(), _modulePackagePaths);
            _validatedModulePlan = plan;
            ModulePlanGrid.ItemsSource = plan.InstallationOrder;
            var terminalsCompatible = plan.InstallationOrder.All(module => module.TerminalCompatibility.IsCompatible);
            InstallModuleButton.IsEnabled = plan.PlanValid && plan.InstallationOrder.Count > 0 && terminalsCompatible;
            ModuleStatus.Text = InstallModuleButton.IsEnabled
                ? $"Plan válido. Se instalarán {plan.InstallationOrder.Count} módulo(s). Todas las terminales registradas reportan una versión compatible."
                : terminalsCompatible
                    ? "El servidor no devolvió un plan instalable."
                    : "Instalación bloqueada: actualiza e inicia sesión en todas las terminales registradas. Las terminales sin versión o incompatibles se muestran en la tabla.";
        }
        catch (Exception exception) { _validatedModulePlan = null; ModulePlanGrid.ItemsSource = null; ModuleStatus.Text = PosUserMessage.ForModuleException(exception); }
        finally { ValidateModuleButton.IsEnabled = true; }
    }

    private async void InstallModule_Click(object sender, RoutedEventArgs e)
    {
        if (_validatedModulePlan is null || !_validatedModulePlan.PlanValid) { ModuleStatus.Text = "Primero valida el paquete."; return; }
        InstallModuleButton.IsEnabled = false;
        ValidateModuleButton.IsEnabled = false;
        try
        {
            var result = await _viewModel.InstallModuleAsync(ModuleIdBox.Text.Trim(), _modulePackagePaths);
            ModuleStatus.Text = $"Instalación registrada como {result.Status}. Respaldo verificado: {result.BackupPath} (SHA-256 {result.BackupSha256}). Pulsa «Reiniciar servidor y cargar módulos» para activar la versión instalada.";
            _validatedModulePlan = null;
            await LoadInstalledModulesAsync();
        }
        catch (Exception exception) { ModuleStatus.Text = PosUserMessage.ForModuleException(exception); }
        finally { ValidateModuleButton.IsEnabled = true; }
    }

    private async void RestartModuleHost_Click(object sender, RoutedEventArgs e)
    {
        if (_restartLocalServiceAsync is null)
        {
            ModuleStatus.Text = "Reinicia el servicio desde la computadora que ejecuta el servidor POS.";
            return;
        }
        if (_viewModel.IsBusy || _viewModel.HasPendingCheckout || _viewModel.CartLines.Count > 0)
        {
            ModuleStatus.Text = "Vacía o termina la venta actual antes de reiniciar el servidor.";
            return;
        }

        var confirmation = MessageBox.Show(this,
            "Se reiniciará el servidor local del POS. Las cajas conectadas perderán conexión durante unos segundos. ¿Continuar?",
            "Reiniciar servidor y activar módulos", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        RestartModuleHostButton.IsEnabled = false;
        ValidateModuleButton.IsEnabled = false;
        InstallModuleButton.IsEnabled = false;
        ModuleStatus.Text = "Deteniendo el servidor anterior y esperando que la versión actual vuelva a estar lista…";
        try
        {
            await _restartLocalServiceAsync(CancellationToken.None);
            if (!await LoadInstalledModulesAsync()) return;
            var modules = (InstalledModulesGrid.ItemsSource as IReadOnlyList<InstalledModuleRecordClient>) ?? [];
            var inactive = modules.FirstOrDefault(module => module.Status is not ("Active" or "Disabled"));
            ModuleStatus.Text = inactive is null
                ? "Servidor reiniciado y listo. Se recargaron los módulos activos en esta caja."
                : $"El servidor reinició, pero '{inactive.ModuleId}' quedó como {inactive.StatusLabel}. {inactive.ActivationDetail}";
        }
        catch (Exception exception)
        {
            ModuleStatus.Text = PosUserMessage.ForModuleException(exception);
        }
        finally
        {
            RestartModuleHostButton.IsEnabled = _restartLocalServiceAsync is not null;
            ValidateModuleButton.IsEnabled = true;
        }
    }

    private async Task<bool> LoadInstalledModulesAsync()
    {
        try
        {
            InstalledModulesGrid.ItemsSource = await _viewModel.GetInstalledModulesAsync();
            UpdateModuleActionButtons();
            return true;
        }
        catch (Exception exception) { ModuleStatus.Text = PosUserMessage.ForModuleException(exception); return false; }
    }

    private void InstalledModulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModuleActionButtons();

    private void UpdateModuleActionButtons()
    {
        DeactivateModuleButton.IsEnabled = InstalledModulesGrid.SelectedItem is InstalledModuleRecordClient { Status: "Active" };
        ActivateModuleButton.IsEnabled = InstalledModulesGrid.SelectedItem is InstalledModuleRecordClient { Status: "Disabled" };
    }

    private async void ActivateModule_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledModulesGrid.SelectedItem is not InstalledModuleRecordClient { Status: "Disabled" } module) return;
        var confirmation = MessageBox.Show(this,
            $"¿Reactivar '{module.ModuleId}' versión {module.Version}? Se conservarán los archivos y datos existentes. Debes reiniciar el servidor POS para cargarlo.",
            "Reactivar módulo", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        ActivateModuleButton.IsEnabled = false;
        try
        {
            await _viewModel.ActivateModuleAsync(module.ModuleId);
            ModuleStatus.Text = _restartLocalServiceAsync is null
                ? $"'{module.ModuleId}' quedó pendiente de activación. Reinicia el servidor desde la computadora que lo ejecuta; no vuelvas a cargar el ZIP."
                : $"'{module.ModuleId}' quedó pendiente de activación. Usa «Reiniciar servidor y cargar módulos» para cargarlo; no se volverán a ejecutar sus migraciones.";
            await LoadInstalledModulesAsync();
        }
        catch (Exception exception)
        {
            var error = PosUserMessage.ForModuleException(exception);
            if (await LoadInstalledModulesAsync()) ModuleStatus.Text = error;
            UpdateModuleActionButtons();
        }
    }

    private async void DeactivateModule_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledModulesGrid.SelectedItem is not InstalledModuleRecordClient { Status: "Active" } module) return;
        var confirmation = MessageBox.Show(this,
            $"¿Desactivar '{module.ModuleId}'? Sus rutas dejarán de responder inmediatamente. Los archivos, tablas e historial se conservarán.",
            "Desactivar módulo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        DeactivateModuleButton.IsEnabled = false;
        try
        {
            await _viewModel.DeactivateModuleAsync(module.ModuleId);
            ModuleStatus.Text = $"Módulo '{module.ModuleId}' desactivado. Sus datos y migraciones se conservaron; el POS base continúa disponible.";
            await LoadInstalledModulesAsync();
        }
        catch (Exception exception)
        {
            ModuleStatus.Text = PosUserMessage.ForException(exception);
            UpdateModuleActionButtons();
        }
    }
}
