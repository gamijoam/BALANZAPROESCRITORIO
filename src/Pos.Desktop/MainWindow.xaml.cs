using System.IO;
using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class MainWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly IPosApiClient _apiClient;
    private readonly Func<CancellationToken, Task>? _restartLocalServiceAsync;
    private readonly bool _stopsLocalServerOnExit;
    private bool _exitConfirmed;
    private readonly List<LoadedDesktopModule> _desktopModules = [];
    private readonly List<MenuItem> _moduleMenuItems = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(PosViewModel viewModel, IPosApiClient apiClient,
        Func<CancellationToken, Task>? restartLocalServiceAsync = null,
        bool stopsLocalServerOnExit = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _apiClient = apiClient;
        _restartLocalServiceAsync = restartLocalServiceAsync;
        _stopsLocalServerOnExit = stopsLocalServerOnExit;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ChargeRequested += ChargeRequested;
        _clock.Tick += (_, _) => ClockLabel.Text = DateTime.Now.ToString("dd/MM/yyyy  HH:mm:ss");
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitConfirmed || !_viewModel.IsAuthenticated) return;
        if (_viewModel.HasPendingCheckout)
        {
            MessageBox.Show(this,
                "Hay una venta cuyo resultado todavía no se ha resuelto. Resuelve esa operación antes de salir.",
                "Venta pendiente", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        var message = _viewModel.CartLines.Count > 0
            ? "El carrito actual se perderá al cerrar el POS. ¿Deseas salir?"
            : "¿Deseas cerrar BalanzaPro?";
        if (_viewModel.CashSession is not null)
            message += Environment.NewLine + Environment.NewLine + "El turno de caja quedará abierto.";
        if (_stopsLocalServerOnExit)
            message += Environment.NewLine + Environment.NewLine + "El servicio local se detendrá y las demás terminales perderán la conexión.";

        var answer = MessageBox.Show(this, message, "Salir de BalanzaPro",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _exitConfirmed = true;
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PosViewModel.CurrentPriceLabel))
        {
            ProductPreviewPrice.Text = _viewModel.CurrentProduct is null ? string.Empty : _viewModel.CurrentPriceLabel;
            ProductSummaryPrice.Text = _viewModel.CurrentProduct is null ? string.Empty : _viewModel.CurrentPriceLabel;
            return;
        }
        if (e.PropertyName != nameof(PosViewModel.CurrentProduct)) return;
        var product = _viewModel.CurrentProduct;
        ProductPreviewName.Text = product?.Name ?? "Sin producto seleccionado";
        ProductPreviewSku.Text = product is null ? string.Empty : $"Código: {product.Sku}";
        ProductPreviewPrice.Text = product is null ? string.Empty : _viewModel.CurrentPriceLabel;
        ProductSummaryCode.Text = product?.Sku ?? string.Empty;
        ProductSummaryName.Text = product?.Name ?? string.Empty;
        ProductSummaryPrice.Text = product is null ? string.Empty : _viewModel.CurrentPriceLabel;
        CurrentProductSummary.Visibility = product is null ? Visibility.Collapsed : Visibility.Visible;
        if (product is not null)
        {
            var selectedLine = _viewModel.CartLines.LastOrDefault(x => x.Product.Id == product.Id);
            if (selectedLine is not null)
            {
                CartGrid.SelectedItem = selectedLine;
                CartGrid.ScrollIntoView(selectedLine);
            }
        }
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
        if (!await SignInAndLoadAsync()) Close();
    }

    private async Task<bool> SignInAndLoadAsync()
    {
        var login = new LoginWindow(_viewModel, TerminalIdentity.GetOrCreate()) { Owner = this };
        if (login.ShowDialog() != true || !_viewModel.IsAuthenticated) return false;
        try
        {
            await _viewModel.LoadInitialPosStateAsync();
            await ReloadDesktopModulesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, PosUserMessage.ForException(ex), "Sesión iniciada con carga incompleta", MessageBoxButton.OK, MessageBoxImage.Warning);
            SearchBox.Focus();
            return true;
        }
        if (_viewModel.CashSession is null) await PromptToOpenCashAsync();
        SearchBox.Focus();
        return true;
    }

    private async void OperatorSessionAction_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        if (_viewModel.HasPendingCheckout)
        {
            MessageBox.Show(this, "Hay una venta pendiente de confirmar. Resuelve el resultado antes de cerrar la sesión.",
                "Venta pendiente", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.CartLines.Count > 0)
        {
            MessageBox.Show(this, "El carrito tiene artículos. Cóbralos o retíralos antes de cambiar de operador.",
                "Carrito con artículos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var currentCash = _viewModel.CashSession;
        var isSwitching = currentCash is not null;
        var confirmation = isSwitching
            ? currentCash!.IsActiveForCurrentOperator
                ? "¿Cambiar de operador? El turno seguirá abierto y quedará disponible para que el siguiente operador autenticado lo reclame. El cambio quedará registrado."
                : $"¿Cambiar de operador? El turno seguirá abierto y continuará asignado a {currentCash.ActiveOperatorName ?? "otro operador"}."
            : $"¿Cerrar la sesión de {_viewModel.OperatorName} y volver al inicio de sesión?";
        var answer = MessageBox.Show(this, confirmation,
            isSwitching ? "Cambiar operador" : "Cerrar sesión", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (isSwitching) await _viewModel.SwitchOperatorAsync();
            else await _viewModel.LogoutAsync();
            if (!await SignInAndLoadAsync()) Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo cambiar el operador", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PromptToOpenCashAsync()
    {
        var dialog = new OpenCashWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _viewModel.OpenCashSessionAsync(dialog.OpeningBalanceUsd, dialog.OpeningBalanceVes, dialog.Currency); }
        catch (PosApiException ex) when (ex.Code == "cash_session_already_open")
        {
            MessageBox.Show(this, "Ya hay un turno abierto en esta terminal. Inicia sesión con el operador activo o pídele que cambie de operador.",
                "Turno de caja existente", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
            var selection = new ProductSelectionWindow(_viewModel.SearchResults.ToArray(), query, id => _viewModel.GetProductImageAsync(id)) { Owner = this };
            if (selection.ShowDialog() == true && selection.SelectedProduct is not null)
                _viewModel.AddProduct(selection.SelectedProduct);
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void RemoveCartLine_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEditCart || (sender as FrameworkElement)?.DataContext is not CartLineViewModel line) return;
        _viewModel.RemoveProduct(line);
    }

    private void ModulesButton_Click(object sender, RoutedEventArgs e)
    {
        ModulesPopup.PlacementTarget = ModulesButton;
        ModulesPopup.IsOpen = !ModulesPopup.IsOpen;
    }

    private void ModulesPopup_Opened(object? sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => ModulesPopup.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.IsEnabled && item.Visibility == Visibility.Visible)?.Focus(), DispatcherPriority.Input);
    }

    private void ModulesPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        ModulesPopup.IsOpen = false;
        ModulesButton.Focus();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
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
                FocusCartQuantity(_viewModel.SelectedCartLine ?? CartGrid.SelectedItem as CartLineViewModel);
                break;
            case Key.Delete when CartGrid.IsKeyboardFocusWithin && !IsCartCellEditing:
                e.Handled = true;
                if (CartGrid.SelectedItem is CartLineViewModel line && _viewModel.CanEditCart) _viewModel.RemoveProduct(line);
                break;
            case Key.F12:
                e.Handled = true;
                await ToggleCashSessionAsync();
                RefocusScanner();
                break;
        }
    }

    private void CartGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Up or Key.Down) && IsCartCellEditing)
        {
            e.Handled = true;
            if (!CartGrid.CommitEdit(DataGridEditingUnit.Cell, true)) return;
            CartGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = CartGrid.SelectedItem as CartLineViewModel;
            var index = selected is null ? -1 : _viewModel.CartLines.IndexOf(selected);
            var next = index + (e.Key == Key.Down ? 1 : -1);
            if (next >= 0 && next < _viewModel.CartLines.Count) FocusCartQuantity(_viewModel.CartLines[next]);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (IsCartCellEditing)
            {
                CartGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                CartGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            else if (CartGrid.SelectedItem is CartLineViewModel selected)
                FocusCartQuantity(selected);
            return;
        }

        if (e.Key == Key.Escape && IsCartCellEditing)
        {
            e.Handled = true;
            CartGrid.CancelEdit(DataGridEditingUnit.Cell);
            CartGrid.CancelEdit(DataGridEditingUnit.Row);
        }
    }

    private void FocusCartQuantity(CartLineViewModel? line)
    {
        if (!_viewModel.CanEditCart || _viewModel.CartLines.Count == 0) return;
        line ??= _viewModel.CartLines.FirstOrDefault();
        if (line is null) return;
        var quantityColumn = CartGrid.Columns.FirstOrDefault(column => string.Equals(column.Header?.ToString(), "Cantidad", StringComparison.Ordinal));
        if (quantityColumn is null) return;

        _viewModel.SelectedCartLine = line;
        CartGrid.SelectedItem = line;
        CartGrid.ScrollIntoView(line, quantityColumn);
        CartGrid.CurrentCell = new DataGridCellInfo(line, quantityColumn);
        CartGrid.Focus();
        CartGrid.BeginEdit();
        Dispatcher.BeginInvoke(() => FindVisualChild<TextBox>(CartGrid)?.SelectAll(), DispatcherPriority.Input);
    }

    private bool IsCartCellEditing => CartGrid.CurrentCell.Column?.GetCellContent(CartGrid.CurrentCell.Item) is TextBox;

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async void CashTool_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        if (_viewModel.CashSession is null) { await PromptToOpenCashAsync(); return; }
        if (!_viewModel.CashSession.IsActiveForCurrentOperator)
        {
            MessageBox.Show(this, $"La caja sigue abierta y está en uso por {_viewModel.CashSession.ActiveOperatorName ?? "otro operador"}. Puedes cerrar sesión o esperar a que ese operador la libere.",
                "Turno en uso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
        finally { RefocusScanner(); }
    }

    private void SaleTool_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        _viewModel.SetStatus("Venta actual. Escanea un código o escribe el nombre del producto.");
    }

    private void SalesHistoryTool_Click(object sender, RoutedEventArgs e)
    {
        new SalesHistoryWindow(_viewModel, CreateReceiptPreviewWindow) { Owner = this }.ShowDialog();
        RefocusScanner();
    }

    private void InventoryTool_Click(object sender, RoutedEventArgs e)
    {
        new InventoryWindow(_viewModel, _desktopModules) { Owner = this }.ShowDialog();
        RefocusScanner();
    }

    public async Task<bool> ReloadDesktopModulesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in _moduleMenuItems) ModulesPopup.Items.Remove(item);
        _moduleMenuItems.Clear();
        ModuleMenuEmpty.Visibility = Visibility.Visible;
        AdminModulesMenuItem.Visibility = Visibility.Collapsed;
        AdminModulesMenuItem.Header = "Administrar módulos";
        try
        {
            var managementAccess = await _apiClient.GetModuleManagementAccessAsync(cancellationToken);
            if (managementAccess.CanOpenManagement)
            {
                AdminModulesMenuItem.Visibility = Visibility.Visible;
                if (managementAccess.RequiresAdministratorPin)
                    AdminModulesMenuItem.Header = "Administrar módulos (requiere PIN)";
            }
        }
        catch (NotSupportedException) { }
        catch (Exception exception)
        {
            _viewModel.SetStatus($"No se pudo consultar el acceso a módulos: {PosUserMessage.ForException(exception)}");
        }

        foreach (var loaded in _desktopModules) loaded.LoadContext.Unload();
        _desktopModules.Clear();
        IReadOnlyList<DesktopModuleClientInfo> descriptors;
        try { descriptors = await _apiClient.GetDesktopModulesAsync(cancellationToken); }
        catch (NotSupportedException) { return true; }
        catch (Exception exception)
        {
            _viewModel.SetStatus($"No se cargaron las pantallas de módulos: {PosUserMessage.ForException(exception)}");
            return false;
        }
        var allModulesLoaded = true;
        foreach (var descriptor in descriptors)
        {
            try
            {
                var bytes = await _apiClient.DownloadDesktopModulePackageAsync(descriptor.ModuleId, cancellationToken);
                var module = PosDesktopModuleLoader.Load(bytes, descriptor, _apiClient, TerminalIdentity.GetOrCreate(),
                    (query, token) => _apiClient.SearchProductsAsync(query, token),
                    () => _desktopModules.SelectMany(loaded => loaded.Registry.ReportSources
                        .Select(source => new PosDesktopReportSourceInstance(loaded.Context, source))).ToArray());
                if (module.Registry.ReportCenter is not null && _desktopModules.Any(item => item.Registry.ReportCenter is not null))
                {
                    module.LoadContext.Unload();
                    throw new InvalidDataException("Solo puede estar activo un centro de reportes avanzado.");
                }
                if (module.Registry.ReceiptExtension is not null && _desktopModules.Any(item => item.Registry.ReceiptExtension is not null))
                {
                    module.LoadContext.Unload();
                    throw new InvalidDataException("Solo puede estar activa una extensión de comprobantes.");
                }
                var registeredReportIds = _desktopModules.SelectMany(item => item.Registry.ReportSources)
                    .Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (module.Registry.ReportSources.Any(item => registeredReportIds.Contains(item.Id)))
                {
                    module.LoadContext.Unload();
                    throw new InvalidDataException("Otro módulo ya registró una fuente con el mismo ID de reporte.");
                }
                _desktopModules.Add(module);
                foreach (var menu in module.Registry.Menus)
                {
                    if (!module.Context.GrantedPermissions.Contains(menu.RequiredPermission)) continue;
                    ModuleMenuEmpty.Visibility = Visibility.Collapsed;
                    var menuItem = new MenuItem
                    {
                        Header = menu.Label,
                        Tag = (module, menu)
                    };
                    System.Windows.Automation.AutomationProperties.SetName(menuItem, menu.Label);
                    System.Windows.Automation.AutomationProperties.SetAutomationId(menuItem, $"Module_{module.ModuleId}_{menu.Id}");
                    menuItem.Click += ModuleMenu_Click;
                    ModulesPopup.Items.Insert(1 + _moduleMenuItems.Count, menuItem);
                    _moduleMenuItems.Add(menuItem);
                }
            }
            catch (Exception exception)
            {
                _viewModel.SetStatus($"El módulo {descriptor.ModuleId} no se cargó: {PosUserMessage.ForException(exception)}");
                allModulesLoaded = false;
            }
        }
        return allModulesLoaded;
    }

    private void ModuleMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ValueTuple<LoadedDesktopModule, PosDesktopMenuContribution> pair }) return;
        var (module, menu) = pair;
        if (!module.Context.GrantedPermissions.Contains(menu.RequiredPermission)) return;
        ModulesPopup.IsOpen = false;
        try
        {
            var window = menu.CreateWindow(module.Context);
            window.Owner = this;
            window.ShowDialog();
        }
        catch (Exception exception) { MessageBox.Show(this, PosUserMessage.ForException(exception), "No se pudo abrir el módulo", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefocusScanner();
    }

    private bool TryGetReceiptExtension(out LoadedDesktopModule module, out PosDesktopReceiptContribution contribution)
    {
        foreach (var loaded in _desktopModules)
        {
            var candidate = loaded.Registry.ReceiptExtension;
            if (candidate is null || !loaded.Context.GrantedPermissions.Contains(candidate.RequiredPermission)) continue;
            module = loaded;
            contribution = candidate;
            return true;
        }
        module = null!;
        contribution = null!;
        return false;
    }

    private Window CreateReceiptPreviewWindow(SaleReceipt receipt)
    {
        if (TryGetReceiptExtension(out var module, out var contribution))
            return contribution.Extension.CreatePreviewWindow(module.Context, receipt);
        return new ReceiptWindow(receipt);
    }

    private void ManagementTool_Click(object sender, RoutedEventArgs e)
    {
        var section = (sender as FrameworkElement)?.Tag?.ToString() ?? "Tasa";
        if (section == "Reportes" && _desktopModules.Select(item => (Module: item, Center: item.Registry.ReportCenter))
            .FirstOrDefault(item => item.Center is not null && item.Module.Context.GrantedPermissions.Contains(item.Center.RequiredPermission)) is { Center: { } reportCenter, Module: var reportModule })
        {
            try
            {
                var window = reportCenter.CreateWindow(reportModule.Context);
                window.Owner = this;
                window.ShowDialog();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, PosUserMessage.ForException(exception), "No se pudo abrir reportes", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            RefocusScanner();
            return;
        }
        new ManagementWindow(_viewModel, section, _restartLocalServiceAsync) { Owner = this }.ShowDialog();
        RefocusScanner();
    }

    private async Task ToggleCashSessionAsync()
    {
        if (_viewModel.CashSession is null)
        {
            await PromptToOpenCashAsync();
            return;
        }
        if (!_viewModel.CashSession.IsActiveForCurrentOperator)
        {
            MessageBox.Show(this, $"La caja está en uso por {_viewModel.CashSession.ActiveOperatorName ?? "otro operador"}. No se puede cerrar desde esta sesión.",
                "Turno en uso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this, "¿Cerrar el turno de caja? No se podrán confirmar más ventas hasta abrir otro.",
            "Cerrar caja", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        CashSessionInfo? currentSession;
        try { currentSession = await _viewModel.RefreshCashSessionAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo consultar el arqueo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (currentSession is null)
        {
            MessageBox.Show(this, "No hay un turno de caja abierto para consultar el efectivo esperado.", "Caja no disponible", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var closeDialog = new CloseCashWindow(currentSession.ExpectedUsd, currentSession.ExpectedVes) { Owner = this };
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
        RefocusScanner();
    }

    private void RefocusScanner()
    {
        if (!IsLoaded || !IsVisible) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void ChargeRequested(object? sender, EventArgs e)
    {
        try
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
            if (TryGetReceiptExtension(out var receiptModule, out var receiptContribution))
            {
                try
                {
                    var print = await receiptContribution.Extension.PrintAsync(receiptModule.Context, receipt);
                    if (!print.Accepted)
                    {
                        var message = string.IsNullOrWhiteSpace(print.Message) ? "Revisa la impresora de esta terminal." : print.Message;
                        _viewModel.SetStatus($"Venta {receipt.SaleId.ToString("N")[..8]} confirmada; impresión pendiente.");
                        MessageBox.Show(this, $"La venta quedó confirmada, pero el ticket no se pudo enviar a la impresora. {message} Puedes reimprimirlo desde Historial de ventas.",
                            "Impresión pendiente", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else _viewModel.SetStatus($"Venta {receipt.SaleId.ToString("N")[..8]} confirmada e impresión enviada a la cola de Windows.");
                }
                catch (Exception printException)
                {
                    _viewModel.SetStatus($"Venta {receipt.SaleId.ToString("N")[..8]} confirmada; impresión pendiente.");
                    MessageBox.Show(this, $"La venta quedó confirmada, pero falló la impresión. {PosUserMessage.ForException(printException)} Puedes reimprimirla desde Historial de ventas.",
                        "Impresión pendiente", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else CreateReceiptPreviewWindow(receipt).ShowDialog();
            SearchBox.Focus();
        }
        catch (ArgumentException ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "Revisa los pagos", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo confirmar la venta", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        finally { RefocusScanner(); }
    }
}

public sealed class CartQuantityValidationRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        if (decimal.TryParse(value?.ToString(), NumberStyles.Number, PosNumberFormat.Culture, out var quantity)
            && quantity > 0m && decimal.Round(quantity, 3) == quantity)
            return ValidationResult.ValidResult;
        return new ValidationResult(false, "La cantidad debe ser mayor que cero y tener hasta tres decimales.");
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
