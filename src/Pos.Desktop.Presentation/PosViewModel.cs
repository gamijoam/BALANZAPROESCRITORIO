using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Pos.Desktop.Presentation;

public sealed class PosViewModel : INotifyPropertyChanged
{
    private readonly IPosApiClient _api;
    private string _searchText = string.Empty;
    private string _statusMessage = "Conecta con el servicio para iniciar la venta.";
    private string _operatorName = string.Empty;
    private bool _isBusy;
    private string _activeTool = "Venta";
    private ProductInfo? _currentProduct;
    private CashSessionInfo? _cashSession;
    private CustomerInfo? _selectedCustomer;
    private Guid? _pendingCustomerId;
    private string? _pendingIdempotencyKey;
    private SaleLineInput[]? _pendingLines;
    private SalePaymentInput[]? _pendingPayments;
    private ReceiptLine[]? _pendingReceiptLines;
    private decimal _pendingTotal;
    private string _pendingCurrency = "VES";
    private Guid? _pendingExchangeRateId;
    private ExchangeRateInfo? _usdToVesRate;

    public PosViewModel(IPosApiClient api)
    {
        _api = api;
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
    public string OperatorName { get => _operatorName; private set => Set(ref _operatorName, value); }
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
            foreach (var line in CartLines) line.SetPricing(Currency, _usdToVesRate);
            OnPropertyChanged(nameof(CurrentUnitPrice)); OnPropertyChanged(nameof(CurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceCurrentPriceLabel)); OnPropertyChanged(nameof(ReferenceTotalLabel));
            RefreshTotals(); NotifyChargeCommand();
        }
    }
    public CustomerInfo? SelectedCustomer { get => _selectedCustomer; private set => Set(ref _selectedCustomer, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(CanCharge)); NotifyChargeCommand(); } } }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_api.AccessToken);
    public bool CanCharge => IsAuthenticated && CashSession is not null && ItemCount > 0 && !IsBusy;
    public bool CanEditCart => _pendingIdempotencyKey is null;
    public bool HasPendingCheckout => _pendingIdempotencyKey is not null;
    public IReadOnlyList<SalePaymentInput>? PendingPayments => _pendingPayments?.ToArray();
    public string CashStatus => CashSession is null ? "Caja cerrada" : $"Caja abierta · {CashSession.Currency}";
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
        IsBusy = true;
        try
        {
            await _api.LoginAsync(username, pin, terminalId, cancellationToken);
            OperatorName = username.Trim();
            await RefreshExchangeRateAsync(cancellationToken);
            CashSession = await _api.GetCurrentCashSessionAsync(cancellationToken);
            StatusMessage = CashSession is null ? "Sesión iniciada. Abre la caja para comenzar." : "Sesión iniciada. Escanea o busca un producto.";
            OnPropertyChanged(nameof(IsAuthenticated)); OnPropertyChanged(nameof(CanCharge));
        }
        catch (PosApiException ex)
        {
            StatusMessage = ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Usuario o PIN incorrecto." : "No se pudo iniciar sesión. Verifica el servicio.";
            throw;
        }
        catch (HttpRequestException) { StatusMessage = "No se pudo conectar con el servicio. Revisa la dirección y la red."; throw; }
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

    public async Task<CashCloseInfo?> CloseCashSessionAsync(decimal countedBalance, CancellationToken cancellationToken = default)
        => await CloseCashSessionAsync(countedBalance, 0m, cancellationToken);

    public async Task<CashCloseInfo?> CloseCashSessionAsync(decimal countedUsd, decimal countedVes, CancellationToken cancellationToken = default)
    {
        if (CashSession is null) return null;
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
        => CashSession is null ? throw new InvalidOperationException("No hay una caja abierta.") : _api.RecordCashMovementAsync(CashSession.Id, movement, cancellationToken);

    public Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default) => _api.GetInventoryAsync(cancellationToken);
    public Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default)
        => _api.CreateProductAsync(sku, name, unitPrice, currency, initialStock, cancellationToken);
    public Task UploadProductImageAsync(Guid productId, string imagePath, CancellationToken cancellationToken = default)
        => _api.UploadProductImageAsync(productId, imagePath, cancellationToken);
    public Task<byte[]?> GetProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
        => _api.GetProductImageAsync(productId, cancellationToken);
    public Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default)
        => _api.AdjustStockAsync(productId, quantityDelta, reason, cancellationToken);
    public Task<IReadOnlyList<StockMovementInfo>> GetStockMovementsAsync(Guid productId, CancellationToken cancellationToken = default)
        => _api.GetStockMovementsAsync(productId, cancellationToken);
    public Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query = null, CancellationToken cancellationToken = default) => _api.GetCustomersAsync(query, cancellationToken);
    public Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default)
        => _api.CreateCustomerAsync(name, documentNumber, phone, currency, creditLimit, cancellationToken);
    public Task ChangePriceAsync(Guid productId, decimal price, string reason, CancellationToken cancellationToken = default) => _api.ChangePriceAsync(productId, price, reason, cancellationToken);
    public Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default) => _api.GetExchangeRatesAsync(cancellationToken);
    public async Task SetExchangeRateAsync(string from, string to, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default)
    {
        await _api.SetExchangeRateAsync(from, to, rate, effectiveAtUtc, source, cancellationToken);
        await RefreshExchangeRateAsync(cancellationToken);
    }
    public Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        => _api.GetSalesReportAsync(fromUtc, toUtc, cancellationToken);
    public Task<SaleDetailReportInfo> GetSalesDetailReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, int page = 1, CancellationToken cancellationToken = default)
        => _api.GetSalesDetailReportAsync(fromUtc, toUtc, currency, page, cancellationToken);
    public Task<CashSessionReportInfo> GetCashSessionReportAsync(DateTime fromUtc, DateTime toUtc, string? status, CancellationToken cancellationToken = default)
        => _api.GetCashSessionReportAsync(fromUtc, toUtc, status, cancellationToken);
    public Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default) => _api.GetOperatorsAsync(cancellationToken);
    public Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default) => _api.CreateOperatorAsync(username, pin, role, cancellationToken);
    public Task SetOperatorRoleAsync(Guid id, string role, CancellationToken cancellationToken = default) => _api.SetOperatorRoleAsync(id, role, cancellationToken);
    public Task SetOperatorActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) => _api.SetOperatorActiveAsync(id, active, cancellationToken);
    public Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default) => _api.GetAuditAsync(cancellationToken);
    public Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default) => _api.GetInventoryReportAsync(cancellationToken);
    public Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default) => _api.GetReceivablesAsync(customerId, cancellationToken);
    public Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string reason, CancellationToken cancellationToken = default)
        => _api.RecordReceivablePaymentAsync(customerId, amount, currency, reason, cancellationToken);
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
        RefreshTotals();
    }

    public async Task<SaleReceipt> CompleteSaleAsync(IReadOnlyList<SalePaymentInput> payments, CancellationToken cancellationToken = default)
    {
        if (!CanCharge || CashSession is null) throw new InvalidOperationException("A signed-in operator and open cash session are required.");
        if (_pendingIdempotencyKey is null)
        {
            if (payments.Count == 0 || payments.Any(x => x.Amount <= 0 || x.Currency is not ("USD" or "VES")))
                throw new ArgumentException("Cada pago debe tener un monto válido y estar expresado en VES o USD.", nameof(payments));
            var paymentTotal = 0m;
            foreach (var payment in payments)
            {
                if (payment.Currency != Currency && _usdToVesRate is null)
                    throw new ArgumentException("Registra una tasa USD/VES vigente para aceptar pagos en ambas monedas.", nameof(payments));
                paymentTotal += ConvertPrice(payment.Amount, payment.Currency, Currency, _usdToVesRate?.Rate);
                if ((string.Equals(payment.Method, "Fiado", StringComparison.OrdinalIgnoreCase) || string.Equals(payment.Method, "credit", StringComparison.OrdinalIgnoreCase)) && payment.Currency != Currency)
                    throw new ArgumentException("El pago fiado debe expresarse en la moneda de la venta.", nameof(payments));
            }
            if (paymentTotal != Total)
                throw new ArgumentException($"Los pagos convertidos suman {PosNumberFormat.FormatAmount(paymentTotal)} {Currency}; el total es {PosNumberFormat.FormatAmount(Total)} {Currency}.", nameof(payments));
            if (payments.Any(x => string.Equals(x.Method, "Fiado", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Method, "credit", StringComparison.OrdinalIgnoreCase)) && SelectedCustomer is null)
                throw new ArgumentException("Selecciona un cliente antes de cobrar una parte al fiado.", nameof(payments));
            _pendingIdempotencyKey = Guid.NewGuid().ToString("N");
            _pendingLines = CartLines.Select(x => new SaleLineInput(x.Product.Id, x.Quantity)).ToArray();
            _pendingPayments = payments.ToArray();
            _pendingCustomerId = SelectedCustomer?.Id;
            _pendingReceiptLines = CartLines.Select(x => new ReceiptLine(x.Product.Sku, x.Product.Name, x.Quantity, x.UnitPrice, x.LineTotal, x.Product.UnitPrice, x.Product.Currency, x.AppliedExchangeRate)).ToArray();
            _pendingTotal = Total;
            _pendingCurrency = Currency;
            _pendingExchangeRateId = CartLines.Any(x => x.ExchangeRateId.HasValue) || payments.Any(x => x.Currency != Currency) ? _usdToVesRate?.Id : null;
            OnPropertyChanged(nameof(CanEditCart)); OnPropertyChanged(nameof(HasPendingCheckout)); OnPropertyChanged(nameof(PendingPayments)); NotifyCartCommands();
        }
        var lines = _pendingLines!;
        var fixedPayments = _pendingPayments!;
        var receiptLines = _pendingReceiptLines!;
        IsBusy = true;
        try
        {
            var saleId = await _api.CheckoutAsync(CashSession.Id, lines, fixedPayments, _pendingCustomerId, _pendingIdempotencyKey, _pendingExchangeRateId, cancellationToken);
            var receipt = new SaleReceipt(saleId, DateTime.Now, _pendingCurrency, _pendingTotal, receiptLines, fixedPayments);
            ClearPendingCheckout();
            CartLines.Clear(); CurrentProduct = null; SelectedCustomer = null; RefreshTotals();
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
    private void Decrease(CartLineViewModel? line) { if (line is null || !CanEditCart) return; if (line.Quantity <= 1) CartLines.Remove(line); else line.Quantity--; RefreshTotals(); }
    private void RefreshTotals() { OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(ReferenceTotalLabel)); OnPropertyChanged(nameof(ItemCount)); OnPropertyChanged(nameof(Currency)); OnPropertyChanged(nameof(CanCharge)); NotifyChargeCommand(); }
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
    private void ClearPendingCheckout()
    {
        _pendingIdempotencyKey = null; _pendingLines = null; _pendingPayments = null; _pendingReceiptLines = null; _pendingCustomerId = null; _pendingExchangeRateId = null;
        OnPropertyChanged(nameof(CanEditCart)); OnPropertyChanged(nameof(HasPendingCheckout)); OnPropertyChanged(nameof(PendingPayments)); NotifyCartCommands();
    }
    private void NotifyChargeCommand() => (ChargeCommand as RelayCommand)?.NotifyCanExecuteChanged();
    private void NotifyCartCommands()
    {
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
    public event PropertyChangedEventHandler? PropertyChanged;
    public ProductInfo Product { get; } = product;
    public string SettlementCurrency => _settlementCurrency;
    public decimal UnitPrice => PosViewModel.ConvertPrice(Product.UnitPrice, Product.Currency, SettlementCurrency, _usdToVesRate?.Rate);
    public Guid? ExchangeRateId => Product.Currency == SettlementCurrency ? null : _usdToVesRate?.Id;
    public decimal? AppliedExchangeRate => Product.Currency == SettlementCurrency || _usdToVesRate is null
        ? null
        : Product.Currency == "USD" && SettlementCurrency == "VES" ? _usdToVesRate.Rate : 1m / _usdToVesRate.Rate;
    public void SetPricing(string currency, ExchangeRateInfo? rate)
    {
        _settlementCurrency = currency; _usdToVesRate = rate;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettlementCurrency)));
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
    public decimal LineTotal => decimal.Round(UnitPrice * Quantity, 2, MidpointRounding.AwayFromZero);
}
