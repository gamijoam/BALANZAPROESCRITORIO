using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class MainWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(PosViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ChargeRequested += ChargeRequested;
        _clock.Tick += (_, _) => ClockLabel.Text = DateTime.Now.ToString("dd/MM/yyyy  HH:mm:ss");
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PosViewModel.CurrentProduct)) return;
        var product = _viewModel.CurrentProduct;
        ProductPreviewName.Text = product?.Name ?? "Sin producto seleccionado";
        ProductPreviewImage.Source = null;
        ProductPreviewImage.Visibility = Visibility.Collapsed;
        ProductPreviewPlaceholder.Visibility = Visibility.Visible;
        if (product is null || !product.HasImage) return;
        try
        {
            var bytes = await _viewModel.GetProductImageAsync(product.Id);
            if (bytes is null || _viewModel.CurrentProduct?.Id != product.Id) return;
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            ProductPreviewImage.Source = image;
            ProductPreviewImage.Visibility = Visibility.Visible;
            ProductPreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { /* La venta sigue disponible si una foto no se puede cargar. */ }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var login = new LoginWindow(_viewModel, TerminalIdentity.GetOrCreate()) { Owner = this };
        if (login.ShowDialog() != true || !_viewModel.IsAuthenticated)
        {
            Close();
            return;
        }
        if (_viewModel.CashSession is null) await PromptToOpenCashAsync();
        SearchBox.Focus();
    }

    private async Task PromptToOpenCashAsync()
    {
        var dialog = new OpenCashWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _viewModel.OpenCashSessionAsync(dialog.OpeningBalanceUsd, dialog.OpeningBalanceVes, dialog.Currency); }
        catch (Exception ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo abrir la caja", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAndRefocusAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAndRefocusAsync();
    }

    private async Task SearchAndRefocusAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            _viewModel.SetStatus("Escribe un código o nombre para buscar.");
            SearchBox.Focus();
            return;
        }

        await _viewModel.SearchAsync(query);
        if (_viewModel.SearchResults.Count > 0)
        {
            var selection = new ProductSelectionWindow(_viewModel.SearchResults.ToArray(), query) { Owner = this };
            if (selection.ShowDialog() == true && selection.SelectedProduct is not null)
                _viewModel.AddProduct(selection.SelectedProduct);
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F1:
                e.Handled = true;
                Customer_Click(this, new RoutedEventArgs());
                break;
            case Key.F2:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.F4:
                e.Handled = true;
                if (_viewModel.ChargeCommand.CanExecute(null)) _viewModel.ChargeCommand.Execute(null);
                break;
            case Key.F8:
                e.Handled = true;
                if (CartGrid.SelectedItem is CartLineViewModel line) _viewModel.RemoveProduct(line);
                break;
            case Key.F12:
                e.Handled = true;
                await ToggleCashSessionAsync();
                break;
        }
    }

    private async void CashTool_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CashSession is null) { await PromptToOpenCashAsync(); return; }
        var actions = new CashActionsWindow(_viewModel.CashSession) { Owner = this };
        if (actions.ShowDialog() != true) return;
        if (actions.CloseCashRequested)
        {
            await ToggleCashSessionAsync();
            return;
        }

        var dialog = new CashMovementWindow(_viewModel.CashSession.Currency) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Movement is null) return;
        try { await _viewModel.RecordCashMovementAsync(dialog.Movement); MessageBox.Show(this, "Movimiento guardado.", "Caja", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo registrar el movimiento", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SaleTool_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        _viewModel.SetStatus("Venta actual. Escanea un código o escribe el nombre del producto.");
    }

    private void InventoryTool_Click(object sender, RoutedEventArgs e) => new InventoryWindow(_viewModel) { Owner = this }.ShowDialog();

    private void ManagementTool_Click(object sender, RoutedEventArgs e)
    {
        var section = (sender as FrameworkElement)?.Tag?.ToString() ?? "Tasa";
        new ManagementWindow(_viewModel, section) { Owner = this }.ShowDialog();
    }

    private async Task ToggleCashSessionAsync()
    {
        if (_viewModel.CashSession is null)
        {
            await PromptToOpenCashAsync();
            return;
        }
        var answer = MessageBox.Show(this, "¿Cerrar el turno de caja? No se podrán confirmar más ventas hasta abrir otro.",
            "Cerrar caja", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var closeDialog = new CloseCashWindow(_viewModel.CashSession.Currency) { Owner = this };
        if (closeDialog.ShowDialog() != true) return;
        try
        {
            var close = await _viewModel.CloseCashSessionAsync(closeDialog.CountedUsd, closeDialog.CountedVes);
            if (close is not null) MessageBox.Show(this, $"USD esperado: {close.ExpectedUsd:N2} | contado: {close.CountedUsd:N2} | diferencia: {close.VarianceUsd:N2}\nVES esperado: {close.ExpectedVes:N2} | contado: {close.CountedVes:N2} | diferencia: {close.VarianceVes:N2}", "Arqueo de caja", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo cerrar la caja", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Customer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CustomersWindow(_viewModel) { Owner = this };
        if (dialog.ShowDialog() == true) _viewModel.SelectCustomer(dialog.SelectedCustomer);
    }

    private async void ChargeRequested(object? sender, EventArgs e)
    {
        var pendingPayments = _viewModel.PendingPayments ?? [];
        decimal? rate = null;
        if (pendingPayments.Count == 0)
        {
            try { rate = (await _viewModel.RefreshCheckoutRateAsync())?.Rate; }
            catch (Exception ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo consultar la tasa", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        var dialog = new PaymentWindow(_viewModel.Total, _viewModel.Currency, pendingPayments, rate) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var receipt = await _viewModel.CompleteSaleAsync(dialog.Payments);
            new ReceiptWindow(receipt) { Owner = this }.ShowDialog();
            SearchBox.Focus();
        }
        catch (ArgumentException ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "Revisa los pagos", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo confirmar la venta", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

internal static class TerminalIdentity
{
    public static Guid GetOrCreate()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POS");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "terminal-id.txt");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing)) return existing;
        var id = Guid.NewGuid();
        File.WriteAllText(path, id.ToString("D"));
        return id;
    }
}
