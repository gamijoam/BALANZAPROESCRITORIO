using System.Windows;
using System.Windows.Controls;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class SalesHistoryWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly Func<SaleReceipt, Window>? _receiptPreviewFactory;
    private IReadOnlyList<SaleDetailRow> _sales = [];
    private int _page = 1;
    private int _totalCount;
    private int _pageSize = 250;
    private DateTime _fromUtc;
    private DateTime _toUtc;

    public SalesHistoryWindow(PosViewModel viewModel, Func<SaleReceipt, Window>? receiptPreviewFactory = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _receiptPreviewFactory = receiptPreviewFactory;
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        Loaded += async (_, _) => await LoadSalesAsync();
    }

    private string CurrencyFilter => (CurrencyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() is { } value && value != "Todas" ? value : string.Empty;
    private string PaymentMethodFilter => (PaymentMethodFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() is { } value && value != "Todos los métodos" ? value : string.Empty;
    private string SaleStatusFilter => (SaleStatusFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() is { } value && value != "Todos los estados" ? value : string.Empty;
    private SaleDetailRow? SelectedSale => (SalesGrid.SelectedItem as SalesHistoryListItem)?.Sale;

    private async void Search_Click(object sender, RoutedEventArgs e)
        => await Search_ClickAsync();

    private async Task LoadSalesAsync(Guid? selectSaleId = null)
    {
        try
        {
            if (_fromUtc == default || _toUtc == default)
            {
                var today = DateTime.Today;
                var zone = TimeZoneInfo.Local;
                _fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(today.AddDays(-30), DateTimeKind.Unspecified), zone);
                _toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Unspecified), zone);
            }

            SalesGrid.IsEnabled = false;
            StatusText.Text = "Consultando ventas…";
            var result = await _viewModel.GetSalesDetailReportAsync(_fromUtc, _toUtc, CurrencyFilter, _page,
                paymentMethod: PaymentMethodFilter, operatorName: OperatorFilterBox.Text.Trim(), status: SaleStatusFilter);
            _sales = result.Sales;
            _totalCount = result.TotalCount;
            _pageSize = result.PageSize;
            ApplySearchFilter();
            var pages = Math.Max(1, (_totalCount + _pageSize - 1) / _pageSize);
            PageLabel.Text = $"Página {result.Page} de {pages}";
            PreviousPageButton.IsEnabled = result.Page > 1;
            NextPageButton.IsEnabled = result.Page < pages;
            SalesSummary.Text = $"{_totalCount} ventas en el período · {result.Sales.Count} en esta página";
            StatusText.Text = $"Período: {FromDatePicker.SelectedDate:dd/MM/yyyy} al {ToDatePicker.SelectedDate:dd/MM/yyyy}. Selecciona una venta para ver el comprobante.";

            if (selectSaleId.HasValue)
            {
                var match = SalesGrid.Items.Cast<SalesHistoryListItem>().FirstOrDefault(x => x.Sale.Id == selectSaleId.Value);
                if (match is not null) SalesGrid.SelectedItem = match;
            }
        }
        catch (Exception exception)
        {
            _sales = [];
            SalesGrid.ItemsSource = null;
            ClearPreview();
            StatusText.Text = PosUserMessage.ForException(exception);
            SalesSummary.Text = "No se pudieron cargar las ventas.";
        }
        finally { SalesGrid.IsEnabled = true; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchFilter();

    private async void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        CurrencyPicker.SelectedIndex = 0;
        PaymentMethodFilterBox.SelectedIndex = 0;
        SaleStatusFilterBox.SelectedIndex = 0;
        OperatorFilterBox.Clear();
        SearchBox.Clear();
        await Search_ClickAsync();
    }

    private async Task Search_ClickAsync()
    {
        var fromDate = (FromDatePicker.SelectedDate ?? DateTime.Today).Date;
        var toDate = (ToDatePicker.SelectedDate ?? DateTime.Today).Date;
        if (toDate < fromDate)
        {
            StatusText.Text = "La fecha final debe ser igual o posterior a la fecha inicial.";
            return;
        }
        if ((toDate - fromDate).TotalDays >= 92)
        {
            StatusText.Text = "El período máximo por consulta es de 92 días.";
            return;
        }
        var zone = TimeZoneInfo.Local;
        _fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromDate, DateTimeKind.Unspecified), zone);
        _toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Unspecified), zone);
        _page = 1;
        await LoadSalesAsync();
    }

    private void ApplySearchFilter()
    {
        if (SalesGrid is null) return;
        var term = SearchBox.Text.Trim();
        var rows = _sales.Where(sale => term.Length == 0 ||
            sale.Id.ToString("N").Contains(term, StringComparison.OrdinalIgnoreCase) ||
            sale.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            sale.OperatorName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            sale.Lines.Any(line => line.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) || line.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(sale => new SalesHistoryListItem(sale)).ToArray();
        SalesGrid.ItemsSource = rows;
        if (rows.Length == 0) ClearPreview();
    }

    private void SalesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedSale is not { } sale)
        {
            ClearPreview();
            return;
        }

        SaleSelectionSummary.Text = $"Venta {sale.Id.ToString("N")[..8].ToUpperInvariant()} · {sale.StatusLabel} · Devuelto: {sale.Currency} {PosNumberFormat.FormatAmount(sale.ReturnedAmount)}";
        ReceiptPayments.Text = sale.Payments.Count == 0
            ? "Sin pagos registrados."
            : string.Join(Environment.NewLine, sale.Payments.Select(payment => payment.DescribeForSaleCurrency(sale.Currency)));
        PreviewLinesGrid.ItemsSource = sale.Lines;
        PrintReceiptButton.IsEnabled = true;
        ReturnSaleButton.IsEnabled = sale.Status != "Returned" && sale.Lines.Any(x => x.ReturnableQuantity > 0m);
    }

    private void ClearPreview()
    {
        if (SaleSelectionSummary is null) return;
        SaleSelectionSummary.Text = "Selecciona una venta para ver sus artículos.";
        ReceiptPayments.Text = string.Empty;
        PreviewLinesGrid.ItemsSource = null;
        PrintReceiptButton.IsEnabled = false;
        ReturnSaleButton.IsEnabled = false;
    }

    private void PrintReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSale is not { } sale) return;
        var receipt = SaleHistoryReceiptMapper.ToReceipt(sale);
        var preview = _receiptPreviewFactory?.Invoke(receipt) ?? new ReceiptWindow(receipt);
        preview.Owner = this;
        preview.ShowDialog();
    }

    private async void ReturnSale_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSale is not { } sale) return;
        var window = new SaleReturnWindow(_viewModel, sale) { Owner = this };
        if (window.ShowDialog() != true) return;
        await LoadSalesAsync(sale.Id);
        StatusText.Text = "Devolución registrada. El historial y las cantidades pendientes se actualizaron.";
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        await LoadSalesAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page * _pageSize >= _totalCount) return;
        _page++;
        await LoadSalesAsync();
    }

    private static DateTime ToLocalTime(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class SalesHistoryListItem
    {
        public SalesHistoryListItem(SaleDetailRow sale) => Sale = sale;
        public SaleDetailRow Sale { get; }
        public string Number => Sale.Id.ToString("N")[..8].ToUpperInvariant();
        public DateTime LocalDate => ToLocalTime(Sale.CreatedAtUtc);
        public string CustomerName => Sale.CustomerName;
        public string TotalLabel => $"{Sale.Currency} {PosNumberFormat.FormatAmount(Sale.Total)}";
        public string ReferenceLabel => Sale.HistoricalReferenceLabel;
        public string PaymentsLabel => Sale.PaymentCurrenciesLabel;
        public string StatusLabel => Sale.StatusLabel;
    }
}
