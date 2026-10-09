using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;

namespace Pos.Desktop.Presentation;

public sealed class PosApiClient : IPosApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private string? _accessToken;

    public PosApiClient(Uri baseAddress, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress
    {
        get => _httpClient.BaseAddress ?? throw new InvalidOperationException("API base address is missing.");
        set => _httpClient.BaseAddress = new Uri(value.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public string? AccessToken => _accessToken;

    public async Task<LoginResult> LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/v1/auth/login", new { username, pin, terminalId }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty login response.");
        _accessToken = result.AccessToken;
        return new LoginResult(result.OperatorId, result.TerminalId, result.ExpiresAtUtc, result.AccessToken);
    }

    public async Task<CashSessionInfo?> GetCurrentCashSessionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/cash-sessions/current");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<CashSessionResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty cash session response.");
        return new CashSessionInfo(result.Id, result.Currency, result.OpeningBalance, result.OpenedAtUtc, result.OpeningBalanceUsd, result.OpeningBalanceVes);
    }

    public async Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default)
        => await OpenCashSessionAsync(currency == "USD" ? openingBalance : 0m, currency == "VES" ? openingBalance : 0m, currency, cancellationToken);

    public async Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalanceUsd, decimal openingBalanceVes, string settlementCurrency, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/cash-sessions");
        request.Content = JsonContent.Create(new { openingBalanceUsd, openingBalanceVes, currency = settlementCurrency }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<CashSessionResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty cash session response.");
        return new CashSessionInfo(result.Id, result.Currency, result.OpeningBalance, result.OpenedAtUtc, result.OpeningBalanceUsd, result.OpeningBalanceVes);
    }

    public async Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingBalance, CancellationToken cancellationToken = default)
        => await CloseCashSessionAsync(cashSessionId, countedClosingBalance, 0m, cancellationToken);

    public async Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingUsd, decimal countedClosingVes, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/cash-sessions/{cashSessionId}/close");
        request.Content = JsonContent.Create(new { countedClosingUsd, countedClosingVes }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<CashCloseResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty cash close response.");
        return new CashCloseInfo(result.ExpectedBalance, result.CountedBalance, result.Variance,
            result.ExpectedUsd, result.CountedUsd, result.VarianceUsd, result.ExpectedVes, result.CountedVes, result.VarianceVes);
    }

    public async Task RecordCashMovementAsync(Guid cashSessionId, CashMovementInput movement, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/cash-sessions/{cashSessionId}/movements");
        request.Content = JsonContent.Create(movement, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/inventory");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<InventoryItemInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/products");
        request.Content = JsonContent.Create(new { sku, name, unitPrice, currency, initialStock }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ProductCreateResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty product response.");
        return new InventoryItemInfo(result.Id, result.Sku, result.Name, result.UnitPrice, result.Currency, result.InitialStock, result.HasImage);
    }

    public async Task UploadProductImageAsync(Guid productId, string imagePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(imagePath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(extension switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "application/octet-stream" });
        content.Add(fileContent, "file", Path.GetFileName(imagePath));
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/products/{productId}/image"); request.Content = content;
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<byte[]?> GetProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/products/{productId}/image");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/inventory/{productId}/adjustments");
        request.Content = JsonContent.Create(new { quantityDelta, reason }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<StockAdjustmentResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty stock adjustment response.");
        return result.Quantity;
    }

    public async Task<IReadOnlyList<StockMovementInfo>> GetStockMovementsAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/inventory/{productId}/movements");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<StockMovementInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/customers?query=" + Uri.EscapeDataString(query ?? string.Empty));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<CustomerInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/customers");
        request.Content = JsonContent.Create(new { name, documentNumber, phone, currency, creditLimit }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CustomerInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty customer response.");
    }

    public async Task ChangePriceAsync(Guid productId, decimal newPrice, string reason, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/products/{productId}/price-changes");
        request.Content = JsonContent.Create(new { newPrice, reason }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/exchange-rates");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<ExchangeRateInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task SetExchangeRateAsync(string fromCurrency, string toCurrency, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/exchange-rates");
        request.Content = JsonContent.Create(new { fromCurrency, toCurrency, rate, effectiveAtUtc = DateTime.SpecifyKind(effectiveAtUtc, DateTimeKind.Utc), source }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/sales?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SalesReportInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty sales report.");
    }

    public async Task<SaleDetailReportInfo> GetSalesDetailReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, int page = 1, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/sales/detail?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}&page={Math.Max(1, page)}&pageSize=250&currency={Uri.EscapeDataString(string.IsNullOrWhiteSpace(currency) ? "Todas" : currency)}";
        using var request = CreateRequest(HttpMethod.Get, path); using var response = await _httpClient.SendAsync(request, cancellationToken); await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SaleDetailReportInfo>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("El servicio devolvió un reporte vacío.");
    }

    public async Task<CashSessionReportInfo> GetCashSessionReportAsync(DateTime fromUtc, DateTime toUtc, string? status, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/cash-sessions?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}&page=1&pageSize=250&status={Uri.EscapeDataString(string.IsNullOrWhiteSpace(status) ? "Todas" : status)}";
        using var request = CreateRequest(HttpMethod.Get, path); using var response = await _httpClient.SendAsync(request, cancellationToken); await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CashSessionReportInfo>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("El servicio devolvió un reporte vacío.");
    }

    public async Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/operators");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<OperatorInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/operators");
        request.Content = JsonContent.Create(new { username, pin, role }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetOperatorRoleAsync(Guid operatorId, string role, CancellationToken cancellationToken = default)
        => await SendPatchAsync($"api/v1/operators/{operatorId}/role", new { role }, cancellationToken);

    public async Task SetOperatorActiveAsync(Guid operatorId, bool active, CancellationToken cancellationToken = default)
        => await SendPatchAsync($"api/v1/operators/{operatorId}/active", new { active }, cancellationToken);

    public async Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/audit?limit=200");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<AuditInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/reports/inventory");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<InventoryReportRow>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/customers/{customerId}/receivables");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReceivableInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty receivable response.");
    }

    public async Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string reason, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/customers/{customerId}/payments");
        request.Content = JsonContent.Create(new { amount, currency, reason }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task SendPatchAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Patch, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/products?query=" + Uri.EscapeDataString(query));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<ProductInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<Guid> CheckoutAsync(Guid cashSessionId, IReadOnlyList<SaleLineInput> lines,
        IReadOnlyList<SalePaymentInput> payments, Guid? customerId, string idempotencyKey, Guid? exchangeRateId = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/sales");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new
        {
            cashSessionId,
            checkout = new { lines, payments, customerId, exchangeRateId }
        }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<SaleResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty sale response.");
        return result.SaleId;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_accessToken)) return;
        using var request = CreateRequest(HttpMethod.Post, "api/v1/auth/logout");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) await EnsureSuccessAsync(response, cancellationToken);
        _accessToken = null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_accessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = "service_error";
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                code = error.GetString() ?? code;
            else if (document.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                code = title.GetString() ?? code;
        }
        catch (JsonException) { }
        throw new PosApiException(response.StatusCode, code);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record LoginResponse(Guid OperatorId, Guid TerminalId, DateTime ExpiresAtUtc, string AccessToken);
    private sealed record CashSessionResponse(Guid Id, string Currency, decimal OpeningBalance, DateTime OpenedAtUtc, decimal OpeningBalanceUsd = 0m, decimal OpeningBalanceVes = 0m);
    private sealed record CashCloseResponse(Guid CashSessionId, decimal ExpectedBalance, decimal CountedBalance, decimal Variance,
        decimal ExpectedUsd = 0m, decimal CountedUsd = 0m, decimal VarianceUsd = 0m, decimal ExpectedVes = 0m, decimal CountedVes = 0m, decimal VarianceVes = 0m);
    private sealed record StockAdjustmentResponse(Guid Id, Guid ProductId, decimal QuantityDelta, decimal Quantity, string? Reason, DateTime OccurredAtUtc);
    private sealed record ProductCreateResponse(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, decimal InitialStock, bool HasImage = false);
    private sealed record SaleResponse(Guid SaleId, bool Replayed);
}
