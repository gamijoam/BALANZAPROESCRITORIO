using System.IO;
using System.Diagnostics;
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
    private bool _exitSaveInProgress;
    private bool _licenseAccessBlocked;
    private readonly List<LoadedDesktopModule> _desktopModules = [];
    private readonly List<MenuItem> _moduleMenuItems = [];
    private readonly Dictionary<Guid, ImageSource?> _productImageCache = [];
    private readonly HashSet<Guid> _productImageLoadsInProgress = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _licenseNoticeTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _licenseNoticeRefreshInProgress;

    public MainWindow(PosViewModel viewModel, IPosApiClient apiClient,
        Func<CancellationToken, Task>? restartLocalServiceAsync = null,
        bool stopsLocalServerOnExit = false,
        bool startSessionOnLoad = true)
    {
        InitializeComponent();
        if (!startSessionOnLoad) Loaded -= Window_Loaded;
        _viewModel = viewModel;
        _apiClient = apiClient;
        _restartLocalServiceAsync = restartLocalServiceAsync;
        _stopsLocalServerOnExit = stopsLocalServerOnExit;
        DataContext = viewModel;
        MainMenuPopup.DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ChargeRequested += ChargeRequested;
        _clock.Tick += (_, _) => ClockLabel.Text = DateTime.Now.ToString("dd/MM/yyyy  HH:mm:ss");
        _clock.Start();
        _licenseNoticeTimer.Tick += LicenseNoticeTimer_Tick;
        Closed += (_, _) => _clock.Stop();
        Closed += (_, _) => _licenseNoticeTimer.Stop();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.PlacementTarget = MenuButton;
        MainMenuPopup.IsOpen = !MainMenuPopup.IsOpen;
        if (MainMenuPopup.IsOpen)
            Dispatcher.BeginInvoke(() => FirstNavigationButton.Focus(), DispatcherPriority.Input);
    }

    private void NavigationMenu_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.IsOpen = false;
        if (sender is not FrameworkElement { Tag: string section }) return;
        switch (section)
        {
            case "Venta": SaleTool_Click(sender, e); break;
            case "Historial": SalesHistoryTool_Click(sender, e); break;
            case "Caja": CashTool_Click(sender, e); break;
            case "Clientes": Customer_Click(sender, e); break;
            case "Inventario": InventoryTool_Click(sender, e); break;
            case "Sesion": OperatorSessionAction_Click(sender, e); break;
            case "Actualizar POS": UpdatePos_Click(sender, e); break;
            case "Actualizar módulos": new ManagementWindow(_viewModel, "Módulos", _restartLocalServiceAsync) { Owner = this }.ShowDialog(); RefocusScanner(); break;
            default: ManagementTool_Click(sender, e); break;
        }
    }

    private async void UpdatePos_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || _viewModel.HasPendingCheckout || _viewModel.CartLines.Count > 0)
        {
            MessageBox.Show(this, "Termina la venta actual antes de actualizar el POS.", "Actualización pendiente",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new PosUpdateWindow { Owner = this };
        if (window.ShowDialog() != true || window.PreparedPackageDirectory is not { } package) return;
        try
        {
            await _viewModel.FlushWorkspaceSyncAsync();
            var installationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            var helper = Path.Combine(installationRoot, "scripts", "Install-DownloadedPosUpdate.ps1");
            if (!File.Exists(helper)) throw new FileNotFoundException("Falta el instalador de actualizaciones. Usa Actualizar POS.cmd una vez para instalar una versión reciente.", helper);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = installationRoot };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(helper);
            start.ArgumentList.Add("-PackageDirectory");
            start.ArgumentList.Add(package);
            start.ArgumentList.Add("-DesktopProcessId");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Process.Start(start) is null) throw new InvalidOperationException("No se pudo iniciar el instalador externo.");
            _exitConfirmed = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"El POS sigue abierto. No se inició la instalación. {ex.Message}",
                "No se pudo actualizar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        var isWide = ActualWidth >= 1200;
        var isCompact = ActualWidth < 960;
        SummaryColumn.Width = isWide ? new GridLength(360) : new GridLength(0);
        SummaryGapColumn.Width = isWide ? new GridLength(16) : new GridLength(0);
        WideSummaryPanel.Visibility = isWide ? Visibility.Visible : Visibility.Collapsed;
        CompactSummaryPanel.Visibility = isWide ? Visibility.Collapsed : Visibility.Visible;

        SearchBox.FontSize = isCompact ? 18 : 20;
        SearchButton.Content = isCompact ? "Buscar" : "Buscar · F2";
        PausedSalesButton.Content = isCompact ? "Pausadas · F7" : "Ventas pausadas · F7";
        MenuButton.Content = isCompact ? "☰" : "☰  Menú";
        EditSelectedLineButton.Content = isCompact ? "Editar" : "Editar línea";
        ChangeQuantityButton.Content = isCompact ? "Cantidad · F8" : "Cambiar cantidad · F8";
        CartGrid.RowStyle = (Style)FindResource(isCompact ? "Pos.CartCompactRow" : "Pos.CartRow");

        var hideProductDetails = ActualHeight < 650;
        SelectedProductRow.Height = hideProductDetails ? new GridLength(0) : new GridLength(isCompact ? 74 : 82);
        ProductPreviewPanel.Visibility = hideProductDetails ? Visibility.Collapsed : Visibility.Visible;
        ShortcutHints.Text = isCompact
            ? "F1 Cliente · F2 Buscar · F4 Cobrar · F6 Pausar · F8 Cantidad · F12 Caja"
            : "F1 Cliente   ·   F2 Buscar   ·   F4 Cobrar   ·   F6 Pausar   ·   F8 Cantidad   ·   F12 Caja";
    }

    private async void PauseSale_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanPauseSale)
        {
            MessageBox.Show(this, "Agrega artículos y resuelve cualquier operación pendiente antes de pausar la venta.",
                "No se puede pausar ahora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.RequiresCartPriceConfirmation)
        {
            MessageBox.Show(this, "Confirma primero los valores actuales del carrito. Después podrás pausarlo o cobrarlo.",
                "Revisar valores", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new PauseSaleWindow(_viewModel.ItemCount, _viewModel.Total, _viewModel.Currency) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _viewModel.PauseCurrentSaleAsync(dialog.Note);
            SearchBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"La venta sigue en pantalla y no se marcó como pausada.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                "No se pudo pausar la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PausedSales_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanViewPausedSales)
        {
            MessageBox.Show(this, "Espera a que termine la operación actual antes de abrir las ventas pausadas.",
                "Ventas pausadas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.RequiresCartPriceConfirmation)
        {
            MessageBox.Show(this, "Confirma primero los valores actuales del carrito antes de cambiar a otra venta.",
                "Revisar valores", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.CartLines.Count > 0)
        {
            var answer = MessageBox.Show(this,
                "Hay artículos en la venta actual. ¿Quieres guardarla como pausada y abrir la lista?",
                "Pausar la venta actual", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            try { await _viewModel.PauseCurrentSaleAsync(null); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"La venta actual permanece en pantalla.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                    "No se pudo pausar la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var dialog = new PausedSalesWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
        SearchBox.Focus();
    }

    private async void AcceptCartValues_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AcceptCurrentCartValues();
        try { await _viewModel.FlushWorkspaceSyncAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se guardaron los valores confirmados. El carrito sigue en pantalla y no se puede cobrar hasta reintentar.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                "No se pudo actualizar el carrito", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitSaveInProgress) { e.Cancel = true; return; }
        if (_exitConfirmed || _licenseAccessBlocked || !_viewModel.IsAuthenticated) return;
        if (_viewModel.HasPendingCheckout)
        {
            MessageBox.Show(this,
                "Hay una venta cuyo resultado todavía no se ha resuelto. Resuelve esa operación antes de salir.",
                "Venta pendiente", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        var message = _viewModel.CartLines.Count > 0
            ? "El carrito está guardado en esta terminal y se recuperará cuando abras el POS otra vez. No se ha cobrado ni se apartó inventario. ¿Deseas salir?"
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
        if (_viewModel.IsWorkspaceSyncPending)
        {
            e.Cancel = true;
            _exitSaveInProgress = true;
            IsEnabled = false;
            try
            {
                await _viewModel.FlushWorkspaceSyncAsync();
                _exitConfirmed = true;
                _exitSaveInProgress = false;
                Close();
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                MessageBox.Show(this,
                    $"No se cerró el POS porque el carrito todavía no se confirmó en el servidor. La venta sigue en pantalla.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                    "No se pudo guardar el carrito", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _exitSaveInProgress = false; }
            return;
        }
        _exitConfirmed = true;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PosViewModel.RequiresCartPriceConfirmation))
        {
            AcceptCartValuesButton.Visibility = _viewModel.RequiresCartPriceConfirmation ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (e.PropertyName == nameof(PosViewModel.CartWorkspaceStatus))
        {
            CartWorkspaceStatusLabel.ToolTip = _viewModel.CartWorkspaceStatus;
            return;
        }
        if (e.PropertyName == nameof(PosViewModel.SelectedCartLine) || e.PropertyName == nameof(PosViewModel.CurrentPriceLabel))
        {
            UpdateSelectedProductPreview(_viewModel.SelectedCartLine);
            return;
        }
        if (e.PropertyName != nameof(PosViewModel.CurrentProduct)) return;
        var product = _viewModel.CurrentProduct;
        if (product is not null)
        {
            var selectedLine = _viewModel.CartLines.LastOrDefault(x => x.Product.Id == product.Id);
            if (selectedLine is not null)
            {
                _viewModel.SelectedCartLine = selectedLine;
                CartGrid.SelectedItem = selectedLine;
                CartGrid.ScrollIntoView(selectedLine);
                return;
            }
        }
        UpdateSelectedProductPreview(_viewModel.SelectedCartLine);
    }

    private void CartGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CartGrid.SelectedItem is not CartLineViewModel line) return;
        _viewModel.SelectedCartLine = line;
        UpdateSelectedProductPreview(line);
    }

    private void UpdateSelectedProductPreview(CartLineViewModel? line)
    {
        ProductPreviewName.Text = line?.Product.Name ?? "Selecciona un producto";
        ProductPreviewSku.Text = line is null ? string.Empty : $"{line.Product.Sku}  ·  {line.SaleUnit.Label}  ·  {line.PriceTierLabel}";
        ProductPreviewPrice.Text = line is null ? string.Empty : $"{line.SettlementCurrency} {line.UnitPrice:N2}";
        ProductPreviewImage.Source = null;
        ProductPreviewImage.Tag = line?.Product.Id;
        SetProductImageVisibility(ProductPreviewImage, false);
        if (line?.Product.HasImage == true) _ = LoadProductImageAsync(line.Product.Id);
    }

    private async Task LoadProductImageAsync(Guid productId)
    {
        if (_productImageCache.ContainsKey(productId))
        {
            ApplyCachedProductImage(productId);
            return;
        }
        if (!_productImageLoadsInProgress.Add(productId)) return;

        try
        {
            var bytes = await _viewModel.GetProductImageAsync(productId);
            if (bytes is null || bytes.Length == 0)
            {
                _productImageCache[productId] = null;
            }
            else
            {
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                _productImageCache[productId] = image;
            }
        }
        catch (Exception)
        {
            // Una foto no debe bloquear la búsqueda ni la venta.
            _productImageCache[productId] = null;
        }
        finally { _productImageLoadsInProgress.Remove(productId); }

        ApplyCachedProductImage(productId);
    }

    private void ApplyCachedProductImage(Guid productId)
    {
        if (!_productImageCache.TryGetValue(productId, out var source)) return;
        if (ProductPreviewImage.Tag is Guid previewId && previewId == productId)
        {
            ProductPreviewImage.Source = source;
            SetProductImageVisibility(ProductPreviewImage, source is not null);
        }
        foreach (var image in FindVisualChildren<Image>(CartGrid))
        {
            if (image.Tag is not Guid imageId || imageId != productId) continue;
            image.Source = source;
            SetProductImageVisibility(image, source is not null);
        }
    }

    private static void SetProductImageVisibility(Image image, bool hasImage)
    {
        image.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        if (image.Parent is not Grid parent) return;
        foreach (var placeholder in parent.Children.OfType<TextBlock>())
            placeholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void LoadCartRowImage(DataGridRow row)
    {
        if (row.Item is not CartLineViewModel line) return;
        var image = FindVisualChild<Image>(row);
        if (image is null) return;
        image.Tag = line.Product.Id;
        image.Source = null;
        SetProductImageVisibility(image, false);
        if (line.Product.HasImage) await LoadProductImageAsync(line.Product.Id);
        System.Windows.Automation.AutomationProperties.SetName(image, $"Imagen de {line.Product.Name}");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        if (!await SignInAndLoadAsync()) Close();
    }

    private async Task<bool> SignInAndLoadAsync()
    {
        var login = new LoginWindow(_viewModel, TerminalIdentity.GetOrCreate()) { Owner = this };
        if (login.ShowDialog() != true || !_viewModel.IsAuthenticated) return false;
        try
        {
            var licenseNotice = await _viewModel.GetLicenseNoticeAsync();
            if (RequiresLicenseRecovery(licenseNotice))
            {
                _licenseAccessBlocked = true;
                var licenseSetupIncomplete = licenseNotice.State is "Missing" or "NotConfigured";
                var trustKeyMissing = licenseNotice.State == "NotConfigured";
                MessageBox.Show(this,
                    trustKeyMissing
                        ? "Este POS aún no está configurado para validar licencias. Contacta al vendedor para completar la configuración."
                        : licenseSetupIncomplete
                            ? "Este POS necesita una licencia activa para continuar. La caja permanecerá bloqueada hasta completar la activación."
                            : "La licencia de este POS no está activa. La caja permanecerá bloqueada hasta renovarla o recuperarla.",
                    licenseSetupIncomplete ? "Se requiere una licencia" : "Licencia no activa",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                if (!await EnsureLicenseAdministratorSessionAsync()) return false;

                new ManagementWindow(_viewModel, "Licencia", _restartLocalServiceAsync, licenseRecoveryOnly: true)
                    { Owner = this }.ShowDialog();

                LicenseNoticeClient refreshedNotice;
                try { refreshedNotice = await _viewModel.GetLicenseNoticeAsync(); }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        $"No se pudo verificar el estado actualizado de la licencia. El POS seguirá bloqueado.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                        "Licencia sin verificar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    try { await _viewModel.LogoutAsync(); } catch (Exception) { }
                    return false;
                }

                if (RequiresLicenseRecovery(refreshedNotice))
                {
                    MessageBox.Show(this,
                        "La licencia sigue sin estar activa. El POS permanecerá bloqueado; puedes volver a iniciar sesión cuando tengas una renovación o un archivo de desbloqueo.",
                        "Licencia no activa", MessageBoxButton.OK, MessageBoxImage.Warning);
                    try { await _viewModel.LogoutAsync(); } catch (Exception) { }
                    return false;
                }

                _licenseAccessBlocked = false;
            }

            await _viewModel.LoadInitialPosStateAsync();
            await RefreshLicenseNoticeAsync();
            _licenseNoticeTimer.Start();
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

    private static bool RequiresLicenseRecovery(LicenseNoticeClient notice) =>
        (notice.State is "Locked" or "Suspended") ||
        (notice.EnforcementEnabled && notice.State is not ("Active" or "Grace" or "Recovery"));

    private async Task<bool> EnsureLicenseAdministratorSessionAsync()
    {
        var terminalId = TerminalIdentity.GetOrCreate();
        const string instruction = "La licencia no está activa. Inicia sesión con una cuenta administradora para acceder únicamente a su recuperación.";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _viewModel.GetLicenseStatusAsync();
                return true;
            }
            catch (PosApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                try { await _viewModel.LogoutAsync(); }
                catch (Exception logoutException)
                {
                    MessageBox.Show(this, PosUserMessage.ForException(logoutException), "No se pudo cambiar de usuario",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (attempt == 2)
                {
                    MessageBox.Show(this,
                        "No se inició sesión con una cuenta administradora. El acceso a la caja permanecerá bloqueado.",
                        "Se requiere una cuenta administradora", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var administratorLogin = new LoginWindow(_viewModel, terminalId, instruction) { Owner = this };
                if (administratorLogin.ShowDialog() != true || !_viewModel.IsAuthenticated) return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"No se pudo validar el acceso de recuperación. El POS permanecerá bloqueado.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                    "Recuperación de licencia", MessageBoxButton.OK, MessageBoxImage.Warning);
                try { await _viewModel.LogoutAsync(); } catch (Exception) { }
                return false;
            }
        }

        return false;
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

    private async void LicenseNoticeTimer_Tick(object? sender, EventArgs e)
        => await RefreshLicenseNoticeAsync();

    private async Task RefreshLicenseNoticeAsync()
    {
        if (_licenseNoticeRefreshInProgress || !_viewModel.IsAuthenticated) return;
        _licenseNoticeRefreshInProgress = true;
        try
        {
            var notice = await _viewModel.GetLicenseNoticeAsync();
            var now = DateTimeOffset.UtcNow;
            var expires = notice.ExpiresAtUtc;
            string? message = null;
            var background = Color.FromRgb(255, 244, 214);
            var foreground = Color.FromRgb(101, 75, 18);
            var border = Color.FromRgb(228, 189, 89);

            switch (notice.State)
            {
                case "Active" when expires is { } activeExpiry && activeExpiry - now <= TimeSpan.FromDays(5):
                {
                    var remaining = activeExpiry - now;
                    var timeText = remaining.TotalHours < 24 ? "menos de un día" : $"{Math.Ceiling(remaining.TotalDays):0} días";
                    message = $"AVISO DE LICENCIA · Vence el {activeExpiry.ToLocalTime():dd/MM/yyyy HH:mm}; quedan {timeText}. " +
                              "Conecta el equipo servidor POS a Internet para renovar automáticamente. Puedes seguir trabajando sin Internet mientras la licencia esté vigente.";
                    break;
                }
                case "Grace" when expires is { } graceExpiry:
                {
                    var graceEnds = graceExpiry.AddDays(2);
                    var remaining = graceEnds - now;
                    var timeText = remaining.TotalHours < 24 ? "menos de un día" : $"{Math.Ceiling(remaining.TotalDays):0} días";
                    background = Color.FromRgb(255, 235, 196);
                    foreground = Color.FromRgb(112, 65, 0);
                    border = Color.FromRgb(225, 151, 42);
                    message = $"LICENCIA EN PERÍODO DE GRACIA · Venció el {graceExpiry.ToLocalTime():dd/MM/yyyy}. " +
                              $"Quedan {timeText} de los 2 días de gracia. Conecta el equipo servidor POS a Internet para renovar; no necesitas importar un archivo cada mes.";
                    break;
                }
                case "Suspended":
                    background = Color.FromRgb(255, 224, 224);
                    foreground = Color.FromRgb(126, 31, 36);
                    border = Color.FromRgb(213, 89, 94);
                    message = "LICENCIA SUSPENDIDA · El acceso a ventas está restringido. Contacta con el vendedor para resolverlo.";
                    break;
                case "Recovery":
                    background = Color.FromRgb(255, 235, 196);
                    foreground = Color.FromRgb(112, 65, 0);
                    border = Color.FromRgb(225, 151, 42);
                    message = notice.RecoveryExpiresAtUtc is { } recoveryExpiry
                        ? $"DESBLOQUEO DE EMERGENCIA · Esta autorización temporal vence el {recoveryExpiry.ToLocalTime():dd/MM/yyyy HH:mm}. Al vencer se volverá a aplicar el estado de licencia actual. Conecta el servidor POS a Internet o contacta a administración antes de esa hora."
                        : "DESBLOQUEO DE EMERGENCIA · Esta autorización temporal tiene una duración limitada. Al vencer se volverá a aplicar el estado de licencia actual. Conecta el servidor POS a Internet o contacta a administración.";
                    break;
                case "Locked":
                    background = Color.FromRgb(255, 224, 224);
                    foreground = Color.FromRgb(126, 31, 36);
                    border = Color.FromRgb(213, 89, 94);
                    message = expires is { } lockedExpiry
                        ? $"LICENCIA VENCIDA · Venció el {lockedExpiry.ToLocalTime():dd/MM/yyyy} y terminaron los 2 días de gracia. El POS limita operaciones nuevas y permite el cierre seguro. Conecta el servidor POS a Internet para renovar o contacta a soporte."
                        : "LICENCIA VENCIDA · Terminaron los 2 días de gracia. El POS limita operaciones nuevas y permite el cierre seguro. Conecta el servidor POS a Internet para renovar o contacta a soporte.";
                    break;
                case "Missing":
                    message = notice.EnforcementEnabled
                        ? "LICENCIA SIN INSTALAR · Importa la licencia inicial para continuar."
                        : "LICENCIA SIN INSTALAR · Instala la licencia desde Administración > Licencia. Este aviso no impide trabajar mientras el bloqueo automático esté desactivado.";
                    break;
                case "NotConfigured":
                    message = "LICENCIA SIN CONFIGURAR · Contacta al vendedor para completar la configuración de este POS.";
                    break;
                case "Invalid":
                case "ClockRollback":
                    background = Color.FromRgb(255, 224, 224);
                    foreground = Color.FromRgb(126, 31, 36);
                    border = Color.FromRgb(213, 89, 94);
                    message = "NO SE PUDO VALIDAR LA LICENCIA · Detén nuevas operaciones y contacta a soporte para revisar el archivo, la hora y la configuración de esta instalación.";
                    break;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                LicenseNoticeBanner.Visibility = Visibility.Collapsed;
                return;
            }

            LicenseNoticeBanner.Background = new SolidColorBrush(background);
            LicenseNoticeBanner.BorderBrush = new SolidColorBrush(border);
            LicenseNoticeText.Foreground = new SolidColorBrush(foreground);
            LicenseNoticeText.Text = message;
            LicenseNoticeBanner.Visibility = Visibility.Visible;
        }
        catch (NotSupportedException)
        {
            LicenseNoticeBanner.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // El aviso no debe interferir con la venta si el servidor local no responde.
        }
        finally
        {
            _licenseNoticeRefreshInProgress = false;
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
                _viewModel.AddProduct(selection.SelectedProduct, selection.SelectedSaleUnit);
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void RemoveCartLine_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEditCart || (sender as FrameworkElement)?.DataContext is not CartLineViewModel line) return;
        _viewModel.RemoveProduct(line);
    }

    private async void EditCartLine_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEditCart || (sender as FrameworkElement)?.DataContext is not CartLineViewModel line) return;
        await EditCartLineAsync(line);
    }

    private async void EditSelectedCartLine_Click(object sender, RoutedEventArgs e)
    {
        var line = _viewModel.SelectedCartLine ?? CartGrid.SelectedItem as CartLineViewModel;
        if (!_viewModel.CanEditCart || line is null) return;
        await EditCartLineAsync(line);
    }

    private async Task EditCartLineAsync(CartLineViewModel line)
    {
        if (line.AvailableSaleUnits.Count <= 1)
        {
            try { await _viewModel.RefreshCartLineSaleUnitsAsync(line); }
            catch (Exception ex) { _viewModel.SetStatus(PosUserMessage.ForException(ex)); }
        }
        var dialog = new CartLineEditWindow(line) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var updated = ReferenceEquals(dialog.SaleUnit, line.SaleUnit)
                ? _viewModel.EditCartLine(line, dialog.Quantity, dialog.PriceTier, dialog.UnitPriceOverride, dialog.PriceOverrideReason)
                : _viewModel.EditCartLine(line, dialog.SaleUnit, dialog.Quantity, dialog.PriceTier, dialog.UnitPriceOverride, dialog.PriceOverrideReason);
            if (updated)
            {
                _viewModel.SelectedCartLine = line;
                CartGrid.SelectedItem = line;
                UpdateSelectedProductPreview(line);
            }
        }
        RefocusScanner();
    }

    private void ChangeSelectedQuantity_Click(object sender, RoutedEventArgs e)
        => FocusCartQuantity(_viewModel.SelectedCartLine ?? CartGrid.SelectedItem as CartLineViewModel);

    private void RemoveSelectedCartLine_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanEditCart) _viewModel.RemoveProduct(_viewModel.SelectedCartLine ?? CartGrid.SelectedItem as CartLineViewModel);
    }

    private void ModulesButton_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.IsOpen = false;
        ModulesPopup.PlacementTarget = MenuButton;
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
        MenuButton.Focus();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape && MainMenuPopup.IsOpen)
        {
            e.Handled = true;
            MainMenuPopup.IsOpen = false;
            MenuButton.Focus();
            return;
        }
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
            case Key.F6:
                e.Handled = true;
                PauseSale_Click(this, new RoutedEventArgs());
                break;
            case Key.F7:
                e.Handled = true;
                PausedSales_Click(this, new RoutedEventArgs());
                break;
            case Key.F8:
                e.Handled = true;
                FocusCartQuantity(_viewModel.SelectedCartLine ?? CartGrid.SelectedItem as CartLineViewModel);
                break;
            case Key.Delete when CartGrid.IsKeyboardFocusWithin:
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

    private void CartGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.ToolTip = e.Row.Item is CartLineViewModel line ? line.Product.Name : null;
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Row.Item is CartLineViewModel rowLine)
            {
                foreach (var button in FindVisualChildren<Button>(e.Row))
                {
                    var action = button.Content?.ToString();
                    if (action == "+")
                        System.Windows.Automation.AutomationProperties.SetName(button, $"Aumentar cantidad de {rowLine.Product.Name}");
                    else if (action == "−")
                        System.Windows.Automation.AutomationProperties.SetName(button, $"Disminuir cantidad de {rowLine.Product.Name}");
                }
            }
            LoadCartRowImage(e.Row);
        }, DispatcherPriority.Loaded);
    }

    private void FocusCartQuantity(CartLineViewModel? line)
    {
        if (!_viewModel.CanEditCart || line is null) return;
        _viewModel.SelectedCartLine = line;
        CartGrid.SelectedItem = line;
        CartGrid.ScrollIntoView(line);
        _ = EditCartLineAsync(line);
    }

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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
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
        var dialog = new PaymentWindow(_viewModel.Total, _viewModel.Currency, pendingPayments, rate, _viewModel.PendingReceivableDueDate) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var receipt = await _viewModel.CompleteSaleAsync(dialog.Payments, receivableDueDate: dialog.ReceivableDueDate);
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
