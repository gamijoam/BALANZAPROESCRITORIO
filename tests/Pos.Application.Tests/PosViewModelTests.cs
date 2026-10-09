using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosViewModelTests
{
    [Fact]
    public void Adding_same_product_twice_increments_one_cart_line_and_decimal_total()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        var product = new ProductInfo(Guid.NewGuid(), "SKU-1", "Arroz", 12.35m, "VES");

        viewModel.AddProduct(product);
        viewModel.AddProduct(product);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(24.70m, line.LineTotal);
        Assert.Equal(2m, viewModel.ItemCount);
        Assert.Equal(24.70m, viewModel.Total);
    }

    [Fact]
    public void Removing_last_unit_removes_line_and_resets_total()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        viewModel.AddProduct(new ProductInfo(Guid.NewGuid(), "SKU-2", "Leche", 4.50m, "VES"));
        var line = Assert.Single(viewModel.CartLines);

        viewModel.RemoveProduct(line);

        Assert.Empty(viewModel.CartLines);
        Assert.Equal(0m, viewModel.Total);
        Assert.Equal(0, viewModel.ItemCount);
    }

    [Fact]
    public async Task Mixed_currency_products_are_added_and_shown_in_the_open_cash_currency()
    {
        var api = new FakePosApiClient { CashCurrency = "USD", CurrentUsdToVesRate = 40m };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());

        viewModel.AddProduct(new ProductInfo(Guid.NewGuid(), "USD-1", "Cable USD", 2m, "USD"));
        viewModel.AddProduct(new ProductInfo(Guid.NewGuid(), "VES-1", "Cable VES", 80m, "VES"));

        Assert.Equal(2, viewModel.CartLines.Count);
        Assert.Equal("USD", viewModel.Currency);
        Assert.Equal(4m, viewModel.Total);
        Assert.Equal(2m, viewModel.CartLines[1].UnitPrice);
        Assert.Equal("USD 2,00", viewModel.ReferenceCurrentPriceLabel);
        Assert.Equal("REF USD 4,00", viewModel.ReferenceTotalLabel);
    }

    [Fact]
    public async Task Retry_after_connection_loss_reuses_the_same_checkout_snapshot()
    {
        var api = new FakePosApiClient();
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(new ProductInfo(Guid.NewGuid(), "SKU-3", "Café", 10m, "VES"));
        var originalPayments = new[] { new SalePaymentInput("cash", 10m, "VES") };

        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.CompleteSaleAsync(originalPayments));
        var lockedPayments = Assert.Single(viewModel.PendingPayments!);
        Assert.False(viewModel.CanEditCart);
        Assert.Equal(10m, lockedPayments.Amount);

        var receipt = await viewModel.CompleteSaleAsync([new SalePaymentInput("transfer", 10m, "VES")]);

        Assert.Equal(api.FirstIdempotencyKey, api.LastIdempotencyKey);
        Assert.Equal("cash", Assert.Single(api.LastPayments!).Method);
        Assert.Equal(10m, receipt.Total);
        Assert.Empty(viewModel.CartLines);
        Assert.False(viewModel.HasPendingCheckout);
    }

    private sealed class FakePosApiClient : IPosApiClient
    {
        private bool _firstCheckout = true;
        public Uri BaseAddress { get; set; } = new("http://localhost:5155");
        public string? AccessToken { get; private set; }
        public string? FirstIdempotencyKey { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public IReadOnlyList<SalePaymentInput>? LastPayments { get; private set; }
        public string CashCurrency { get; set; } = "VES";
        public decimal? CurrentUsdToVesRate { get; set; }
        public Task<LoginResult> LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default)
        {
            AccessToken = "test-token";
            return Task.FromResult(new LoginResult(Guid.NewGuid(), terminalId, DateTime.UtcNow.AddHours(1), AccessToken));
        }
        public Task<CashSessionInfo?> GetCurrentCashSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult<CashSessionInfo?>(new(Guid.NewGuid(), CashCurrency, 0m, DateTime.UtcNow));
        public Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingBalance, CancellationToken cancellationToken = default) => Task.FromResult(new CashCloseInfo(0, countedClosingBalance, 0));
        public Task RecordCashMovementAsync(Guid cashSessionId, CashMovementInput movement, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InventoryItemInfo>>([]);
        public Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default) => Task.FromResult(new InventoryItemInfo(Guid.NewGuid(), sku, name, unitPrice, currency, initialStock));
        public Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default) => Task.FromResult(quantityDelta);
        public Task<IReadOnlyList<StockMovementInfo>> GetStockMovementsAsync(Guid productId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StockMovementInfo>>([]);
        public Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid> CheckoutAsync(Guid cashSessionId, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<SalePaymentInput> payments, Guid? customerId, string idempotencyKey, Guid? exchangeRateId = null, CancellationToken cancellationToken = default)
        {
            FirstIdempotencyKey ??= idempotencyKey;
            LastIdempotencyKey = idempotencyKey;
            LastPayments = payments.ToArray();
            if (_firstCheckout)
            {
                _firstCheckout = false;
                throw new HttpRequestException("Simulated connection loss");
            }
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerInfo>>([]);
        public Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default) => Task.FromResult(new CustomerInfo(Guid.NewGuid(), name, documentNumber, phone, currency, creditLimit));
        public Task ChangePriceAsync(Guid productId, decimal newPrice, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExchangeRateInfo>>(
            CurrentUsdToVesRate is { } rate ? [new ExchangeRateInfo(Guid.Parse("22222222-2222-2222-2222-222222222222"), "USD", "VES", rate, DateTime.UtcNow.AddMinutes(-1), "test", Guid.NewGuid())] : []);
        public Task SetExchangeRateAsync(string fromCurrency, string toCurrency, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) => Task.FromResult(new SalesReportInfo(fromUtc, toUtc, []));
        public Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OperatorInfo>>([]);
        public Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetOperatorRoleAsync(Guid operatorId, string role, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetOperatorActiveAsync(Guid operatorId, bool active, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditInfo>>([]);
        public Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InventoryReportRow>>([]);
        public Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.FromResult(new ReceivableInfo(customerId, "", "VES", 0m, 0m, []));
        public Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
