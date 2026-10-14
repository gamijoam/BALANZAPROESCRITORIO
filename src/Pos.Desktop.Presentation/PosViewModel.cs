using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Pos.Domain.Finance;
using Pos.Domain.Sales;

namespace Pos.Desktop.Presentation;

public sealed class PosViewModel : INotifyPropertyChanged
{
    private readonly IPosApiClient _api;
    private readonly HashSet<CartLineViewModel> _observedCartLines = [];
    private string _searchText = string.Empty;
    private string _statusMessage = "Conecta con el servicio para iniciar la venta.";
    private string _operatorName = string.Empty;
    private bool _isBusy;
    private string _activeTool = "Venta";
    private ProductInfo? _currentProduct;
    private CashSessionInfo? _cashSession;
    private CustomerInfo? _selectedCustomer;
    private CartLineViewModel? _selectedCartLine;
    private Guid? _pendingCustomerId;
    private string? _pendingIdempotencyKey;
    private SaleLineInput[]? _pendingLines;
    private SalePaymentInput[]? _pendingPayments;
    private ReceiptLine[]? _pendingReceiptLines;
    private decimal _pendingTotal;
    private decimal _pendingRoundingAdjustment;
    private string _pendingCurrency = "VES";
    private Guid? _pendingExchangeRateId;
    private ExchangeRateInfo? _usdToVesRate;

    public PosViewModel(IPosApiClient api)
    {
        _api = api;
        CartLines.CollectionChanged += CartLines_CollectionChanged;
        SearchCommand = new AsyncRelayCommand(() => SearchAsync(SearchText), () => !IsBusy && !string.IsNullOrWhiteSpace(SearchText));
        IncreaseLineCommand = new RelayCommand(x => Increase(x as CartLineViewModel), _ => CanEditCart);
        DecreaseLineCommand = new RelayCommand(x => Decrease(x as CartLineViewModel), _ => CanEditCart);
        RemoveLineCommand = new RelayCommand(x => RemoveProduct(x as CartLineViewModel), _ => CanEditCart);
        ChargeCommand = new RelayCommand(_ => ChargeRequested?.Invoke(this, EventArgs.Empty), _ => CanCharge);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ChargeRequested;
    public ObservableCollection<ProductInfo> SearchResults { get; } = [];
    public ObservableCollection<CartLineViewModel> CartLines { get; } = [];
    public ICommand SearchCommand { get; }
    public ICommand IncreaseLineCommand { get; }
    public ICommand DecreaseLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand ChargeCommand { get; }
    public string OperatorName
    {
        get => _operatorName;
        private set
        {
            if (!Set(ref _operatorName, value)) return;
            OnPropertyChanged(nameof(CashStatus));
            OnPropertyChanged(nameof(OperatorSessionActionToolTip));
        }
    }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) (SearchCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged(); } }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string ActiveTool { get => _activeTool; private set => Set(ref _activeTool, value); }
    public ProductInfo? CurrentProduct { get => _currentProduct; private set { if (Set(ref _currentProduct, value)) { OnPropertyChanged(nameof(CurrentUnitPrice)); OnPropertyChanged(nameof(CurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceCurrentPriceLabel)); } } }
    public CashSessionInfo? CashSession
    {
        get => _cashSession;
        private set
        {
            if (!Set(ref _cashSession, value)) return;
            OnPropertyChanged(nameof(CashStatus)); OnPropertyChanged(nameof(Currency)); OnPropertyChanged(nameof(CanCharge));
            OnPropertyChanged(nameof(OperatorSessionActionLabel)); OnPropertyChanged(nameof(CanUseSessionAction));
            OnPropertyChanged(nameof(OperatorSessionActionToolTip));
            foreach (var line in CartLines) line.SetPricing(Currency, _usdToVesRate);
            OnPropertyChanged(nameof(CurrentUnitPrice)); OnPropertyChanged(nameof(CurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceCurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceTotalLabel));
            RefreshTotals(); NotifyChargeCommand();
        }
    }
    public CustomerInfo? SelectedCustomer { get => _selectedCustomer; private set => Set(ref _selectedCustomer, value); }
    public CartLineViewModel? SelectedCartLine
    {
        get => _selectedCartLine;
        set
        {
            if (Set(ref _selectedCartLine, value)) OnPropertyChanged(nameof(CanEditSelectedLine));
        }
    }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(CanCharge)); OnPropertyChanged(nameof(CanUseSessionAction)); OnPropertyChanged(nameof(CanEditSelectedLine)); OnPropertyChanged(nameof(OperatorSessionActionToolTip)); NotifyChargeCommand(); } } }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_api.AccessToken);
    public Uri ApiBaseAddress => _api.BaseAddress;
    public bool CanCharge => IsAuthenticated && CashSession is { IsActiveForCurrentOperator: true } && ItemCount > 0 && !IsBusy;
    public string OperatorSessionActionLabel => CashSession is not null ? "Cambiar operador" : "Cerrar sesión";
    public bool CanUseSessionAction => IsAuthenticated && !IsBusy && CartLines.Count == 0 && !HasPendingCheckout;
    public string OperatorSessionActionToolTip => IsBusy
        ? "Espera a que termine la operación actual."
        : HasPendingCheckout
        ? "Resuelve la venta pendiente antes de cambiar de operador."
        : CartLines.Count > 0
            ? "Cobra o retira los artículos del carrito antes de cambiar de operador."
            : CashSession is null
                ? "Cierra tu sesión y vuelve al inicio de sesión."
                : CashSession.IsActiveForCurrentOperator
                    ? "Cambia de operador manteniendo abierto el turno; quedará disponible para el siguiente usuario."
                    : $"Cierra esta sesión. El turno seguirá asignado a {CashSession.ActiveOperatorName ?? "otro operador"}.";
    public bool CanEditCart => _pendingIdempotencyKey is null;
    public bool CanEditSelectedLine => CanEditCart && !IsBusy && SelectedCartLine is not null;
    public bool HasPendingCheckout => _pendingIdempotencyKey is not null;
    public IReadOnlyList<SalePaymentInput>? PendingPayments => _pendingPayments?.ToArray();
    public string CashStatus => CashSession is null ? "Caja cerrada"
        : CashSession.IsActiveForCurrentOperator
            ? $"Caja abierta · {CashSession.Currency} · turno de {CashSession.OpenedByOperatorName}"
            : $"Caja abierta · en uso por {CashSession.ActiveOperatorName ?? "otro operador"}";
    public decimal Total => CartLines.Sum(x => x.LineTotal);
    public decimal ItemCount => CartLines.Sum(x => x.Quantity);
    public decimal CurrentUnitPrice => CurrentProduct is null ? 0m : ConvertPrice(CurrentProduct.UnitPrice, CurrentProduct.Currency, Currency, _usdToVesRate?.Rate);
    public string CurrentPriceLabel => $"{Currency} {CurrentUnitPrice:N2}";
    public string ReferenceCurrentPriceLabel => FormatReference(CurrentUnitPrice);
    public string ReferenceTotalLabel => $"REF {FormatReference(Total)}";
    public string Currency => CashSession?.Currency ?? CartLines.FirstOrDefault()?.SettlementCurrency ?? "VES";

    public void SetBaseAddress(Uri address) => _api.BaseAddress = address;

    public async Task LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default)
    {
        await AuthenticateAsync(username, pin, terminalId, cancellationToken);
        await LoadInitialPosStateAsync(cancellationToken);
    }

    public async Task AuthenticateAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "Validando usuario y PIN.";
        try
        {
            await _api.LoginAsync(username, pin, terminalId, cancellationToken);
            OperatorName = username.Trim();
            OnPropertyChanged(nameof(IsAuthenticated));
            StatusMessage = "Acceso validado. Preparando el punto de venta.";
        }
        catch (PosApiException ex)
        {
            StatusMessage = ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Usuario o PIN incorrecto."
                : "No se pudo validar el acceso. Verifica el servicio.";
            throw;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "No se pudo conectar con el servicio. Revisa la dirección y la red.";
            throw;
        }
        catch (Exception)
        {
            StatusMessage = "No se pudo validar el acceso. Revisa el servicio e inténtalo de nuevo.";
            throw;
        }
        finally { IsBusy = false; }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await EndOperatorSessionAsync(releaseCashSession: false, cancellationToken);
    }

    public async Task SwitchOperatorAsync(CancellationToken cancellationToken = default)
    {
        await EndOperatorSessionAsync(releaseCashSession: true, cancellationToken);
    }

    private async Task EndOperatorSessionAsync(bool releaseCashSession, CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("Espera a que termine la operación actual antes de cerrar la sesión.");
        if (!releaseCashSession && CashSession is not null)
            throw new InvalidOperationException("Cierra la caja antes de cerrar la sesión del operador.");
        if (HasPendingCheckout)
            throw new InvalidOperationException("Resuelve la venta pendiente antes de cerrar la sesión.");
        if (CartLines.Count > 0)
            throw new InvalidOperationException("Cobra o retira los artículos del carrito antes de cerrar la sesión.");

        IsBusy = true;
        try
        {
            if (releaseCashSession && CashSession is { IsActiveForCurrentOperator: true })
                await _api.ReleaseCurrentCashSessionAsync(cancellationToken);
            await _api.LogoutAsync(cancellationToken);
            OperatorName = string.Empty;
            CashSession = null;
            SelectedCustomer = null;
            SelectedCartLine = null;
            CurrentProduct = null;
            SearchResults.Clear();
            SearchText = string.Empty;
            _usdToVesRate = null;
            StatusMessage = releaseCashSession
                ? "Operador cambiado. Inicia sesión para continuar; la caja sigue abierta."
                : "Sesión cerrada. Inicia sesión para continuar.";
            OnPropertyChanged(nameof(IsAuthenticated));
            OnPropertyChanged(nameof(CanCharge));
            RefreshTotals();
        }
        finally { IsBusy = false; }
    }

    public async Task LoadInitialPosStateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) throw new InvalidOperationException("Authenticate before loading POS session data.");
        IsBusy = true;
        try
        {
            await RefreshExchangeRateAsync(cancellationToken);
            CashSession = await _api.GetCurrentCashSessionAsync(cancellationToken);
            if (CashSession is { ActiveOperatorName: null, IsActiveForCurrentOperator: false })
            {
                try { CashSession = await _api.ClaimCurrentCashSessionAsync(cancellationToken); }
                catch (PosApiException ex) when (ex.Code == "cash_session_in_use")
                { CashSession = await _api.GetCurrentCashSessionAsync(cancellationToken); }
            }
            StatusMessage = CashSession is null
                ? "Sesión iniciada. Abre la caja para comenzar."
                : CashSession.IsActiveForCurrentOperator
                    ? "Sesión iniciada. La caja está lista para continuar."
                    : $"Hay un turno abierto en esta terminal, reservado para {CashSession.ActiveOperatorName ?? "otro operador"}. Ese operador debe liberar el turno antes de que otro pueda usar la caja.";
            OnPropertyChanged(nameof(IsAuthenticated)); OnPropertyChanged(nameof(CanCharge));
        }
        catch (PosApiException ex)
        {
            StatusMessage = ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Acceso aceptado, pero el servicio rechazó la sesión inicial."
                : "Acceso aceptado, pero no se pudieron cargar los datos del punto de venta.";
            throw;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "Acceso aceptado, pero se perdió la conexión al cargar los datos iniciales.";
            throw;
        }
        catch (Exception)
        {
            StatusMessage = "Acceso aceptado, pero ocurrió un error al cargar los datos iniciales.";
            throw;
        }
        finally { IsBusy = false; }
    }

    public async Task OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default)
        => await OpenCashSessionAsync(currency == "USD" ? openingBalance : 0m, currency == "VES" ? openingBalance : 0m, currency, cancellationToken);

    public async Task OpenCashSessionAsync(decimal openingBalanceUsd, decimal openingBalanceVes, string settlementCurrency, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            CashSession = await _api.OpenCashSessionAsync(openingBalanceUsd, openingBalanceVes, settlementCurrency, cancellationToken);
            StatusMessage = "Caja abierta. El lector de códigos está listo.";
        }
        catch (PosApiException) { StatusMessage = "No se pudo abrir la caja. Revisa si ya existe un turno abierto."; throw; }
        finally { IsBusy = false; }
    }

    public async Task<CashSessionInfo?> RefreshCashSessionAsync(CancellationToken cancellationToken = default)
    {
        CashSession = await _api.GetCurrentCashSessionAsync(cancellationToken);
        return CashSession;
    }

    public async Task<CashCloseInfo?> CloseCashSessionAsync(decimal countedBalance, CancellationToken cancellationToken = default)
        => await CloseCashSessionAsync(countedBalance, 0m, cancellationToken);

    public async Task<CashCloseInfo?> CloseCashSessionAsync(decimal countedUsd, decimal countedVes, CancellationToken cancellationToken = default)
    {
        if (CashSession is null) return null;
        if (!CashSession.IsActiveForCurrentOperator) throw new InvalidOperationException($"La caja está en uso por {CashSession.ActiveOperatorName ?? "otro operador"}.");
        IsBusy = true;
        try
        {
            var close = await _api.CloseCashSessionAsync(CashSession.Id, countedUsd, countedVes, cancellationToken);
            CashSession = null;
            StatusMessage = $"Caja cerrada. Diferencias: USD {PosNumberFormat.FormatAmount(close.VarianceUsd)} · VES {PosNumberFormat.FormatAmount(close.VarianceVes)}.";
            return close;
        }
        finally { IsBusy = false; }
    }

    public Task RecordCashMovementAsync(CashMovementInput movement, CancellationToken cancellationToken = default)
        => CashSession is null ? throw new InvalidOperationException("No hay una caja abierta.")
            : !CashSession.IsActiveForCurrentOperator ? throw new InvalidOperationException($"La caja está en uso por {CashSession.ActiveOperatorName ?? "otro operador"}.")
            : _api.RecordCashMovementAsync(CashSession.Id, movement, cancellationToken);

    public Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default) => _api.GetInventoryAsync(cancellationToken);
    public Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(string? query, string status, string currency, string stock, bool? hasImage, CancellationToken cancellationToken = default)
        => _api.GetInventoryAsync(query, status, currency, stock, hasImage, cancellationToken);
    public Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default)
        => _api.CreateProductAsync(sku, name, unitPrice, currency, initialStock, cancellationToken);
    public Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, decimal? price2, decimal? price3,
        string taxTreatment, string currency, decimal initialStock, CancellationToken cancellationToken = default)
        => _api.CreateProductAsync(sku, name, unitPrice, price2, price3, taxTreatment, currency, initialStock, cancellationToken);
    public Task SetProductTaxTreatmentAsync(Guid productId, string taxTreatment, CancellationToken cancellationToken = default)
        => _api.SetProductTaxTreatmentAsync(productId, taxTreatment, cancellationToken);
    public Task UpdateProductAsync(Guid productId, string sku, string name, CancellationToken cancellationToken = default)
        => _api.UpdateProductAsync(productId, sku, name, cancellationToken);
    public Task SetProductActiveAsync(Guid productId, bool active, CancellationToken cancellationToken = default)
        => _api.SetProductActiveAsync(productId, active, cancellationToken);
    public Task UploadProductImageAsync(Guid productId, string imagePath, CancellationToken cancellationToken = default)
        => _api.UploadProductImageAsync(productId, imagePath, cancellationToken);
    public Task<byte[]?> GetProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
        => _api.GetProductImageAsync(productId, cancellationToken);
    public Task RemoveProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
        => _api.RemoveProductImageAsync(productId, cancellationToken);
    public Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default)
        => _api.AdjustStockAsync(productId, quantityDelta, reason, cancellationToken);
    public Task<StockMovementPageInfo> GetStockMovementsAsync(Guid productId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
        => _api.GetStockMovementsAsync(productId, offset, limit, cancellationToken);
    public Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query = null, CancellationToken cancellationToken = default) => _api.GetCustomersAsync(query, cancellationToken);
    public Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default)
        => _api.CreateCustomerAsync(name, documentNumber, phone, currency, creditLimit, cancellationToken);
    public Task<CustomerInfo> UpdateCustomerAsync(Guid customerId, string name, string? documentNumber, string? phone, decimal creditLimit, CancellationToken cancellationToken = default)
        => _api.UpdateCustomerAsync(customerId, name, documentNumber, phone, creditLimit, cancellationToken);
    public Task DeactivateCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
        => _api.DeactivateCustomerAsync(customerId, cancellationToken);
    public Task ChangePriceAsync(Guid productId, decimal price, string reason, CancellationToken cancellationToken = default) => _api.ChangePriceAsync(productId, price, reason, cancellationToken);
    public Task ChangePriceAsync(Guid productId, string priceTier, decimal price, string reason, CancellationToken cancellationToken = default)
        => _api.ChangePriceAsync(productId, priceTier, price, reason, cancellationToken);
    public Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default) => _api.GetExchangeRatesAsync(cancellationToken);
    public async Task SetExchangeRateAsync(string from, string to, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default)
    {
        await _api.SetExchangeRateAsync(from, to, rate, effectiveAtUtc, source, cancellationToken);
        await RefreshExchangeRateAsync(cancellationToken);
    }
    public Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        => _api.GetSalesReportAsync(fromUtc, toUtc, cancellationToken);
    public Task<ProductSalesReportInfo> GetProductSalesReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, CancellationToken cancellationToken = default)
        => _api.GetProductSalesReportAsync(fromUtc, toUtc, currency, cancellationToken);
    public Task<SaleDetailReportInfo> GetSalesDetailReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, int page = 1, CancellationToken cancellationToken = default, string? paymentMethod = null, string? operatorName = null, string? status = null, string? query = null)
        => _api.GetSalesDetailReportAsync(fromUtc, toUtc, currency, page, cancellationToken, paymentMethod, operatorName, status, query);
    public Task<SaleReturnReceipt> ReturnSaleAsync(Guid saleId, SaleReturnInput request, string idempotencyKey, CancellationToken cancellationToken = default)
        => _api.ReturnSaleAsync(saleId, request, idempotencyKey, cancellationToken);
    public Task<CashSessionReportInfo> GetCashSessionReportAsync(DateTime fromUtc, DateTime toUtc, string? status, CancellationToken cancellationToken = default)
        => _api.GetCashSessionReportAsync(fromUtc, toUtc, status, cancellationToken);
    public Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default) => _api.GetOperatorsAsync(cancellationToken);
    public Task<ModuleInstallationPlanClientResult> PlanModuleInstallationAsync(string requestedModuleId, IReadOnlyList<string> packagePaths, CancellationToken cancellationToken = default)
        => _api.PlanModuleInstallationAsync(requestedModuleId, packagePaths, cancellationToken);
    public Task<ModuleInstallationClientResult> InstallModuleAsync(string requestedModuleId, IReadOnlyList<string> packagePaths, CancellationToken cancellationToken = default)
        => _api.InstallModuleAsync(requestedModuleId, packagePaths, cancellationToken);
    public Task<IReadOnlyList<InstalledModuleRecordClient>> GetInstalledModulesAsync(CancellationToken cancellationToken = default)
        => _api.GetInstalledModulesAsync(cancellationToken);
    public Task DeactivateModuleAsync(string moduleId, CancellationToken cancellationToken = default)
        => _api.DeactivateModuleAsync(moduleId, cancellationToken);
    public Task ActivateModuleAsync(string moduleId, CancellationToken cancellationToken = default)
        => _api.ActivateModuleAsync(moduleId, cancellationToken);
    public Task<IReadOnlyList<WarehouseClientInfo>> GetWarehousesAsync(CancellationToken cancellationToken = default)
        => _api.GetWarehousesAsync(cancellationToken);
    public Task<IReadOnlyList<WarehouseStockClientInfo>> GetWarehouseStockAsync(Guid warehouseId, bool includeInactive = false, CancellationToken cancellationToken = default)
        => _api.GetWarehouseStockAsync(warehouseId, includeInactive, cancellationToken);
    public Task<IReadOnlyList<WarehouseTransferClientInfo>> GetWarehouseTransfersAsync(CancellationToken cancellationToken = default)
        => _api.GetWarehouseTransfersAsync(cancellationToken);
    public Task CreateWarehouseAsync(string code, string name, CancellationToken cancellationToken = default)
        => _api.CreateWarehouseAsync(code, name, cancellationToken);
    public Task SetWarehouseActiveAsync(Guid warehouseId, bool active, CancellationToken cancellationToken = default)
        => _api.SetWarehouseActiveAsync(warehouseId, active, cancellationToken);
    public Task CreateWarehouseTransferAsync(Guid sourceWarehouseId, Guid destinationWarehouseId, IReadOnlyList<WarehouseTransferClientLine> lines, CancellationToken cancellationToken = default)
        => _api.CreateWarehouseTransferAsync(sourceWarehouseId, destinationWarehouseId, lines, cancellationToken);
    public Task DispatchWarehouseTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => _api.DispatchWarehouseTransferAsync(transferId, cancellationToken);
    public Task ReceiveWarehouseTransferAsync(Guid transferId, IReadOnlyList<WarehouseReceiptClientLine> lines, CancellationToken cancellationToken = default)
        => _api.ReceiveWarehouseTransferAsync(transferId, lines, cancellationToken);
    public Task AssignTerminalWarehouseAsync(Guid terminalId, Guid warehouseId, CancellationToken cancellationToken = default)
        => _api.AssignTerminalWarehouseAsync(terminalId, warehouseId, cancellationToken);
    public Task<IReadOnlyList<OperatorPermissionSetting>> GetOperatorPermissionsAsync(Guid id, CancellationToken cancellationToken = default) => _api.GetOperatorPermissionsAsync(id, cancellationToken);
    public Task SetOperatorPermissionsAsync(Guid id, IReadOnlyList<OperatorPermissionSetting> permissions, CancellationToken cancellationToken = default) => _api.SetOperatorPermissionsAsync(id, permissions, cancellationToken);
    public Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default) => _api.CreateOperatorAsync(username, pin, role, cancellationToken);
    public Task SetOperatorRoleAsync(Guid id, string role, CancellationToken cancellationToken = default) => _api.SetOperatorRoleAsync(id, role, cancellationToken);
    public Task SetOperatorActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) => _api.SetOperatorActiveAsync(id, active, cancellationToken);
    public Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default) => _api.GetAuditAsync(cancellationToken);
    public Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default) => _api.GetInventoryReportAsync(cancellationToken);
    public Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default) => _api.GetReceivablesAsync(customerId, cancellationToken);
    public Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string? reference, CancellationToken cancellationToken = default)
        => _api.RecordReceivablePaymentAsync(customerId, amount, currency, reference, cancellationToken);
    public Task RecordReceivableOpeningBalanceAsync(Guid customerId, decimal amountUsd, string reason, string idempotencyKey, CancellationToken cancellationToken = default)
        => _api.RecordReceivableOpeningBalanceAsync(customerId, amountUsd, reason, idempotencyKey, cancellationToken);
    public void SelectCustomer(CustomerInfo? customer) { SelectedCustomer = customer; StatusMessage = customer is null ? "Venta general." : $"Cliente: {customer.Name}."; }

    public async Task SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var term = query.Trim();
        if (term.Length == 0) return;
        if (!IsAuthenticated) { StatusMessage = "Inicia sesión para buscar productos."; return; }
        IsBusy = true;
        try
        {
            var results = await _api.SearchProductsAsync(term, cancellationToken);
            SearchResults.Clear();
            foreach (var product in results) SearchResults.Add(product);
            var exact = results.FirstOrDefault(x => string.Equals(x.Sku, term, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                AddProduct(exact);
            }
            else StatusMessage = results.Count == 0 ? "No encontramos ese producto." : $"{results.Count} productos. Selecciona uno para agregarlo.";
        }
        catch (PosApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            StatusMessage = "La sesión venció. Inicia sesión nuevamente.";
            OnPropertyChanged(nameof(IsAuthenticated));
        }
        catch (HttpRequestException) { StatusMessage = "Servicio desconectado. La venta requiere conexión con la caja central."; }
        finally { IsBusy = false; }
    }

    public bool AddProduct(ProductInfo product)
    {
        if (!CanEditCart) { StatusMessage = "Hay una venta pendiente de confirmar. Reintenta antes de cambiar el carrito."; return false; }
        if (!product.IsTaxClassified) { StatusMessage = $"{product.Name} está pendiente de clasificar su impuesto. No se puede vender hasta asignarle una regla."; return false; }
        if (product.StockQuantity is <= 0m) { StatusMessage = $"{product.Name} está agotado y no se puede agregar."; return false; }
        if (product.Currency is not ("USD" or "VES"))
        {
            StatusMessage = "La moneda del producto no está soportada.";
            return false;
        }
        var targetCurrency = CashSession?.Currency ?? CartLines.FirstOrDefault()?.SettlementCurrency ?? product.Currency;
        if (product.Currency != targetCurrency && _usdToVesRate is null)
        {
            StatusMessage = "Registra una tasa USD/VES vigente antes de agregar productos en monedas distintas.";
            return false;
        }
        var line = CartLines.FirstOrDefault(x => x.Product.Id == product.Id);
        if (line is null) CartLines.Add(new CartLineViewModel(product, 1m, targetCurrency, _usdToVesRate));
        else line.Quantity++;
        CurrentProduct = product;
        SearchText = string.Empty;
        SearchResults.Clear();
        OnPropertyChanged(nameof(CurrentUnitPrice)); OnPropertyChanged(nameof(CurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceCurrentPriceLabel));
        RefreshTotals();
        StatusMessage = product.Currency == targetCurrency
            ? $"{product.Name} agregado al carrito."
            : $"{product.Name} convertido de {product.Currency} a {targetCurrency} con la tasa vigente.";
        return true;
    }

    public void RemoveProduct(CartLineViewModel? line)
    {
        if (!CanEditCart) { StatusMessage = "Confirma el resultado de la venta pendiente antes de modificarla."; return; }
        if (line is null) return;
        CartLines.Remove(line);
        if (ReferenceEquals(SelectedCartLine, line)) SelectedCartLine = null;
        UpdatePreviewAfterRemoval(line);
        RefreshTotals();
    }

    public bool EditCartLine(CartLineViewModel? line, decimal quantity, decimal? unitPriceOverride, string? priceOverrideReason)
        => EditCartLine(line, quantity, line?.PriceTier ?? "Price1", unitPriceOverride, priceOverrideReason);

    public bool EditCartLine(CartLineViewModel? line, decimal quantity, string priceTier, decimal? unitPriceOverride, string? priceOverrideReason)
    {
        if (!CanEditCart) { StatusMessage = "Confirma el resultado de la venta pendiente antes de modificarla."; return false; }
        if (line is null || !CartLines.Contains(line)) return false;
        if (priceTier is not ("Price1" or "Price2" or "Price3") ||
            priceTier == "Price2" && line.Product.Price2 is null || priceTier == "Price3" && line.Product.Price3 is null)
        {
            StatusMessage = "Ese nivel de precio no está configurado para el producto.";
            return false;
        }
        if (quantity <= 0m || decimal.Round(quantity, 3) != quantity)
        {
            StatusMessage = "La cantidad debe ser mayor que cero y tener hasta tres decimales.";
            return false;
        }
        var reason = string.IsNullOrWhiteSpace(priceOverrideReason) ? null : priceOverrideReason.Trim();
        if (!SalePriceOverridePolicy.IsValid(unitPriceOverride, reason))
        {
            StatusMessage = "El precio especial debe ser mayor que cero, tener hasta cuatro decimales e indicar un motivo de al menos 3 caracteres.";
            return false;
        }

        line.ApplyEdits(quantity, priceTier, unitPriceOverride, reason);
        SelectedCartLine = line;
        RefreshTotals();
        StatusMessage = unitPriceOverride.HasValue
            ? "Precio especial aplicado a esta venta. Al confirmar, un administrador deberá autorizarlo con su PIN."
            : "Artículo actualizado.";
        return true;
    }

    public async Task<SaleReceipt> CompleteSaleAsync(IReadOnlyList<SalePaymentInput> payments, CancellationToken cancellationToken = default)
    {
        if (!CanCharge || CashSession is null) throw new InvalidOperationException("A signed-in operator and open cash session are required.");
        if (_pendingIdempotencyKey is null)
        {
            if (payments.Count == 0 || payments.Any(x => x.Amount <= 0 || x.Currency is not ("USD" or "VES")))
                throw new ArgumentException("Cada pago debe tener un monto válido y estar expresado en VES o USD.", nameof(payments));
            foreach (var payment in payments)
            {
                if (payment.Currency != Currency && _usdToVesRate is null)
                    throw new ArgumentException("Registra una tasa USD/VES vigente para aceptar pagos en ambas monedas.", nameof(payments));
            }
            if (payments.Any(x => IsCreditPayment(x.Method) && x.Currency == "VES") && _usdToVesRate is null)
                throw new ArgumentException("Registra una tasa USD/VES vigente para convertir el crédito a dólares.", nameof(payments));
            PaymentSettlementResult settlement;
            try
            {
                settlement = PaymentSettlementPolicy.Evaluate(Total, Currency,
                    payments.Select(x => new PaymentTender(x.Amount, x.Currency)), _usdToVesRate?.Rate);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(exception.Message, nameof(payments), exception);
            }
            if (!settlement.IsSettled)
                throw new ArgumentException($"Los pagos difieren del total en {Currency} {PosNumberFormat.FormatAmount(decimal.Abs(settlement.RoundingAdjustment))}, por encima del ajuste de redondeo permitido.", nameof(payments));
            if (payments.Any(x => IsCreditPayment(x.Method)) && SelectedCustomer is null)
                throw new ArgumentException("Selecciona un cliente antes de cobrar una parte a crédito.", nameof(payments));
            _pendingIdempotencyKey = Guid.NewGuid().ToString("N");
            _pendingLines = CartLines.Select(x => new SaleLineInput(x.Product.Id, x.Quantity, x.UnitPriceOverride, x.PriceOverrideReason, x.PriceTier)).ToArray();
            _pendingPayments = payments.ToArray();
            _pendingCustomerId = SelectedCustomer?.Id;
            _pendingReceiptLines = CartLines.Select(x => new ReceiptLine(x.Product.Sku, x.Product.Name, x.Quantity, x.UnitPrice, x.LineTotal,
                x.Product.UnitPrice, x.Product.Currency, x.AppliedExchangeRate, x.PriceTier, x.Product.TaxTreatment, x.TaxRate, x.TaxAmount)).ToArray();
            _pendingTotal = Total;
            _pendingRoundingAdjustment = settlement.RoundingAdjustment;
            _pendingCurrency = Currency;
            _pendingExchangeRateId = CartLines.Any(x => x.ExchangeRateId.HasValue) || payments.Any(x => x.Currency != Currency) ||
                payments.Any(x => IsCreditPayment(x.Method) && x.Currency == "VES") ? _usdToVesRate?.Id : null;
            OnPropertyChanged(nameof(CanEditCart)); OnPropertyChanged(nameof(HasPendingCheckout)); OnPropertyChanged(nameof(PendingPayments)); OnPropertyChanged(nameof(CanUseSessionAction)); OnPropertyChanged(nameof(OperatorSessionActionToolTip)); NotifyCartCommands();
        }
        var lines = _pendingLines!;
        var fixedPayments = _pendingPayments!;
        var receiptLines = _pendingReceiptLines!;
        IsBusy = true;
        try
        {
            var saleId = await _api.CheckoutAsync(CashSession.Id, lines, fixedPayments, _pendingCustomerId, _pendingIdempotencyKey, _pendingExchangeRateId, cancellationToken);
            var receipt = new SaleReceipt(saleId, DateTime.Now, _pendingCurrency, _pendingTotal, receiptLines, fixedPayments)
            { RoundingAdjustment = _pendingRoundingAdjustment };
            ClearPendingCheckout();
            CartLines.Clear(); SelectedCartLine = null; CurrentProduct = null; SelectedCustomer = null; RefreshTotals();
            StatusMessage = $"Venta {saleId.ToString("N")[..8]} confirmada.";
            return receipt;
        }
        catch (PosApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            StatusMessage = ex.Code == "insufficient_stock" ? "No hay existencias suficientes para completar esta venta." : "Conflicto al confirmar. Revisa el estado antes de intentar otro cobro.";
            if (ex.Code == "insufficient_stock") ClearPendingCheckout();
            throw;
        }
        catch (PosApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            if (ex.Code == "exchange_rate_changed")
            {
                try { await RefreshExchangeRateAsync(cancellationToken); } catch (HttpRequestException) { }
            }
            StatusMessage = ex.Code == "exchange_rate_changed"
                ? "La tasa cambió durante el cobro. Se actualizaron los importes; revisa el carrito y vuelve a cobrar."
                : ex.Code == "exchange_rate_unavailable"
                    ? "No hay una tasa USD/VES vigente para convertir este carrito."
                    : "El servicio rechazó la venta por una regla de negocio. Corrige el carrito antes de cobrar de nuevo.";
            ClearPendingCheckout();
            throw;
        }
        catch (HttpRequestException) { StatusMessage = "Se perdió la conexión. Reintenta para consultar el mismo resultado; el carrito queda bloqueado."; throw; }
        finally { IsBusy = false; }
    }

    public void SetStatus(string message) => StatusMessage = message;
    public async Task<ExchangeRateInfo?> RefreshCheckoutRateAsync(CancellationToken cancellationToken = default)
    {
        await RefreshExchangeRateAsync(cancellationToken);
        return _usdToVesRate;
    }

    private void Increase(CartLineViewModel? line) { if (line is null || !CanEditCart) return; line.Quantity++; RefreshTotals(); }
    private void Decrease(CartLineViewModel? line)
    {
        if (line is null || !CanEditCart) return;
        if (line.Quantity <= 1)
        {
            CartLines.Remove(line);
            UpdatePreviewAfterRemoval(line);
        }
        else line.Quantity--;
        RefreshTotals();
    }

    private void UpdatePreviewAfterRemoval(CartLineViewModel removedLine)
    {
        if (CurrentProduct?.Id == removedLine.Product.Id)
            CurrentProduct = CartLines.LastOrDefault()?.Product;
    }
    private void RefreshTotals() { OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(ReferenceTotalLabel)); OnPropertyChanged(nameof(ItemCount)); OnPropertyChanged(nameof(Currency)); OnPropertyChanged(nameof(CanCharge)); OnPropertyChanged(nameof(CanUseSessionAction)); OnPropertyChanged(nameof(OperatorSessionActionToolTip)); NotifyChargeCommand(); }

    private void CartLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var line in _observedCartLines) line.PropertyChanged -= CartLine_PropertyChanged;
            _observedCartLines.Clear();
        }
        if (e.OldItems is not null)
            foreach (CartLineViewModel line in e.OldItems)
                if (_observedCartLines.Remove(line)) line.PropertyChanged -= CartLine_PropertyChanged;
        if (e.NewItems is not null)
            foreach (CartLineViewModel line in e.NewItems)
                if (_observedCartLines.Add(line)) line.PropertyChanged += CartLine_PropertyChanged;
        RefreshTotals();
    }

    private void CartLine_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CartLineViewModel.Quantity) or nameof(CartLineViewModel.LineTotal)) RefreshTotals();
    }
    private string FormatReference(decimal amount)
    {
        if (Currency == "USD") return $"USD {PosNumberFormat.FormatAmount(amount)}";
        if (_usdToVesRate is null) return "USD — (sin tasa)";
        return $"USD {PosNumberFormat.FormatAmount(ConvertPrice(amount, "VES", "USD", _usdToVesRate.Rate))}";
    }
    private async Task RefreshExchangeRateAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var current = (await _api.GetExchangeRatesAsync(cancellationToken))
            .Where(x => ((x.FromCurrency == "USD" && x.ToCurrency == "VES") || (x.FromCurrency == "VES" && x.ToCurrency == "USD")) && x.EffectiveAtUtc <= now)
            .OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).FirstOrDefault();
        _usdToVesRate = current is null ? null : current.FromCurrency == "USD" ? current : current with { FromCurrency = "USD", ToCurrency = "VES", Rate = 1m / current.Rate };
        foreach (var line in CartLines) line.SetPricing(Currency, _usdToVesRate);
        OnPropertyChanged(nameof(CurrentUnitPrice)); OnPropertyChanged(nameof(CurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceCurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceTotalLabel)); RefreshTotals();
    }
    internal static decimal ConvertPrice(decimal price, string from, string to, decimal? usdToVes)
    {
        if (from == to) return decimal.Round(price, 2, MidpointRounding.AwayFromZero);
        if (usdToVes is not > 0m) return 0m;
        var ratio = from == "USD" && to == "VES" ? usdToVes.Value : from == "VES" && to == "USD" ? 1m / usdToVes.Value : throw new InvalidOperationException("Unsupported currency conversion.");
        return decimal.Round(price * ratio, 2, MidpointRounding.AwayFromZero);
    }
    private static bool IsCreditPayment(string method) => string.Equals(method, "credit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method, "fiado", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "credito", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method, "crédito", StringComparison.OrdinalIgnoreCase);
    private void ClearPendingCheckout()
    {
        _pendingIdempotencyKey = null; _pendingLines = null; _pendingPayments = null; _pendingReceiptLines = null; _pendingCustomerId = null; _pendingExchangeRateId = null; _pendingRoundingAdjustment = 0m;
        OnPropertyChanged(nameof(CanEditCart)); OnPropertyChanged(nameof(HasPendingCheckout)); OnPropertyChanged(nameof(PendingPayments)); OnPropertyChanged(nameof(CanUseSessionAction)); OnPropertyChanged(nameof(OperatorSessionActionToolTip)); NotifyCartCommands();
    }
    private void NotifyChargeCommand() => (ChargeCommand as RelayCommand)?.NotifyCanExecuteChanged();
    private void NotifyCartCommands()
    {
        OnPropertyChanged(nameof(CanEditSelectedLine));
        (IncreaseLineCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DecreaseLineCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveLineCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CartLineViewModel(ProductInfo product, decimal quantity, string settlementCurrency = "VES", ExchangeRateInfo? usdToVesRate = null) : INotifyPropertyChanged
{
    private decimal _quantity = quantity;
    private string _settlementCurrency = settlementCurrency;
    private ExchangeRateInfo? _usdToVesRate = usdToVesRate;
    private decimal? _unitPriceOverride;
    private string? _priceOverrideReason;
    private string _priceTier = "Price1";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ProductInfo Product { get; } = product;
    public string SettlementCurrency => _settlementCurrency;
    public string PriceTier => _priceTier;
    public IReadOnlyList<string> AvailablePriceTiers
    {
        get
        {
            var tiers = new List<string> { "Price1" };
            if (Product.Price2.HasValue) tiers.Add("Price2");
            if (Product.Price3.HasValue) tiers.Add("Price3");
            return tiers;
        }
    }
    public decimal CatalogUnitPrice => CatalogUnitPriceFor(PriceTier);
    public decimal CatalogUnitPriceFor(string priceTier)
    {
        var source = priceTier switch { "Price1" => Product.UnitPrice, "Price2" => Product.Price2, "Price3" => Product.Price3, _ => null };
        if (source is null) throw new ArgumentException("El nivel de precio no está configurado.", nameof(priceTier));
        return PosViewModel.ConvertPrice(source.Value, Product.Currency, SettlementCurrency, _usdToVesRate?.Rate);
    }
    public decimal UnitPrice => _unitPriceOverride ?? CatalogUnitPrice;
    public decimal? UnitPriceOverride => _unitPriceOverride;
    public string? PriceOverrideReason => _priceOverrideReason;
    public Guid? ExchangeRateId => Product.Currency == SettlementCurrency ? null : _usdToVesRate?.Id;
    public decimal? AppliedExchangeRate => Product.Currency == SettlementCurrency || _usdToVesRate is null
        ? null
        : Product.Currency == "USD" && SettlementCurrency == "VES" ? _usdToVesRate.Rate : 1m / _usdToVesRate.Rate;
    public void SetPricing(string currency, ExchangeRateInfo? rate)
    {
        _settlementCurrency = currency; _usdToVesRate = rate;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettlementCurrency)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CatalogUnitPrice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitPrice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExchangeRateId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppliedExchangeRate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotal)));
    }
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Quantity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotal)));
        }
    }
    public void ApplyEdits(decimal quantity, decimal? unitPriceOverride, string? priceOverrideReason)
        => ApplyEdits(quantity, PriceTier, unitPriceOverride, priceOverrideReason);

    public void ApplyEdits(decimal quantity, string priceTier, decimal? unitPriceOverride, string? priceOverrideReason)
    {
        _priceTier = priceTier;
        _unitPriceOverride = unitPriceOverride;
        _priceOverrideReason = priceOverrideReason;
        Quantity = quantity;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CatalogUnitPrice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitPrice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnitPriceOverride)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriceOverrideReason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriceTier)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotal)));
    }
    public decimal TaxRate => Product.TaxTreatment switch { "Taxable16" => 0.16m, "Taxable8" => 0.08m, _ => 0m };
    public decimal TaxAmount => decimal.Round(decimal.Round(UnitPrice * Quantity, 2, MidpointRounding.AwayFromZero) * TaxRate, 2, MidpointRounding.AwayFromZero);
    public decimal LineTotal => decimal.Round(UnitPrice * Quantity, 2, MidpointRounding.AwayFromZero) + TaxAmount;
}
