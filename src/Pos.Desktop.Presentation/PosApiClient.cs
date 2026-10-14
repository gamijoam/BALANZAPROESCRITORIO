using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;
using Pos.Shared.Contracts;

namespace Pos.Desktop.Presentation;

public sealed class PosApiClient : IPosApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly string? _terminalCredential;
    private Uri _baseAddress;
    private string? _accessToken;

    public Func<PermissionApprovalChallenge, CancellationToken, Task<AdministratorCredentials?>>? RequestAdministratorPinAsync { get; set; }

    public PosApiClient(Uri baseAddress, HttpClient? httpClient = null, string? terminalCredential = null, string? serverCertificateSha256 = null)
    {
        _httpClient = httpClient ?? CreateHttpClient(serverCertificateSha256);
        _baseAddress = NormalizeBaseAddress(baseAddress);
        _terminalCredential = terminalCredential;
    }

    private static HttpClient CreateHttpClient(string? serverCertificateSha256)
    {
        var client = serverCertificateSha256 is null
            ? new HttpClient()
            : new HttpClient(ServerCertificatePin.CreatePinnedHandler(serverCertificateSha256));
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    public Uri BaseAddress
    {
        get => _baseAddress;
        set => _baseAddress = NormalizeBaseAddress(value);
    }

    public string? AccessToken => _accessToken;

    public static async Task<string> PairTerminalAsync(Uri baseAddress, Guid terminalId, string administratorUsername,
        string administratorPin, CancellationToken cancellationToken = default)
    {
        if (baseAddress.Scheme != Uri.UriSchemeHttps || baseAddress.IsLoopback)
            throw new ArgumentException("El emparejamiento remoto requiere una dirección HTTPS de otra computadora.", nameof(baseAddress));
        if (terminalId == Guid.Empty) throw new ArgumentException("Falta la identidad de esta terminal.", nameof(terminalId));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var endpoint = new Uri(NormalizeBaseAddress(baseAddress), "api/v1/terminals/pair");
        using var response = await client.PostAsJsonAsync(endpoint,
            new { terminalId, administratorUsername, administratorPin }, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "El usuario o PIN de administrador no es válido.",
                HttpStatusCode.BadRequest => "El servidor rechazó la solicitud de emparejamiento. Verifica que uses HTTPS y que su base esté actualizada.",
                _ => $"No se pudo emparejar la terminal. Respuesta del servidor: {(int)response.StatusCode}."
            };
            throw new InvalidOperationException(message);
        }

        var result = await response.Content.ReadFromJsonAsync<TerminalPairResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servidor respondió sin una credencial de terminal.");
        if (result.TerminalCredential is null || result.TerminalCredential.Length != 64 || result.TerminalCredential.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("El servidor devolvió una credencial de terminal inválida.");
        return result.TerminalCredential;
    }

    public async Task<LoginResult> LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default)
    {
        _accessToken = null;
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/v1/auth/login"))
        {
            Content = JsonContent.Create(new { username, pin, terminalId, clientVersion = PosClientCompatibility.Version }, options: JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(_terminalCredential))
            request.Headers.TryAddWithoutValidation("X-POS-Terminal-Credential", _terminalCredential);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty login response.");
        if (string.IsNullOrWhiteSpace(result.AccessToken)) throw new InvalidDataException("The service returned an invalid login token.");
        _accessToken = result.AccessToken;
        return new LoginResult(result.OperatorId, result.TerminalId, result.ExpiresAtUtc, result.AccessToken);
    }

    public async Task<CashSessionInfo?> GetCurrentCashSessionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/cash-sessions/current");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<CashSessionResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty cash session response.");
        return new CashSessionInfo(result.Id, result.Currency, result.OpeningBalance, result.OpenedAtUtc,
            result.OpeningBalanceUsd, result.OpeningBalanceVes, result.ExpectedUsd, result.ExpectedVes,
            result.OpenedByOperatorName ?? "", result.ActiveOperatorName, result.IsActiveForCurrentOperator);
    }

    public async Task<CashSessionInfo?> ClaimCurrentCashSessionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/cash-sessions/current/claim");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await GetCurrentCashSessionAsync(cancellationToken);
    }

    public async Task ReleaseCurrentCashSessionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/cash-sessions/current/release");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default)
        => await OpenCashSessionAsync(currency == "USD" ? openingBalance : 0m, currency == "VES" ? openingBalance : 0m, currency, cancellationToken);

    public async Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalanceUsd, decimal openingBalanceVes, string settlementCurrency, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/cash-sessions");
        request.Content = JsonContent.Create(new { openingBalanceUsd, openingBalanceVes, currency = settlementCurrency }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<CashSessionResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty cash session response.");
        return new CashSessionInfo(result.Id, result.Currency, result.OpeningBalance, result.OpenedAtUtc,
            result.OpeningBalanceUsd, result.OpeningBalanceVes, result.ExpectedUsd, result.ExpectedVes,
            result.OpenedByOperatorName ?? "", result.ActiveOperatorName, result.IsActiveForCurrentOperator);
    }

    public async Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingBalance, CancellationToken cancellationToken = default)
        => await CloseCashSessionAsync(cashSessionId, countedClosingBalance, 0m, cancellationToken);

    public async Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingUsd, decimal countedClosingVes, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/cash-sessions/{cashSessionId}/close");
        request.Content = JsonContent.Create(new { countedClosingUsd, countedClosingVes }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
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
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default)
        => await GetInventoryAsync(null, "active", "", "", null, cancellationToken);

    public async Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(string? query, string status, string currency, string stock, bool? hasImage, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parameters.Add("query=" + Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(status)) parameters.Add("status=" + Uri.EscapeDataString(status));
        if (!string.IsNullOrWhiteSpace(currency)) parameters.Add("currency=" + Uri.EscapeDataString(currency));
        if (!string.IsNullOrWhiteSpace(stock)) parameters.Add("stock=" + Uri.EscapeDataString(stock));
        if (hasImage.HasValue) parameters.Add("hasImage=" + hasImage.Value.ToString().ToLowerInvariant());
        var suffix = parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters);
        using var request = CreateRequest(HttpMethod.Get, "api/v1/inventory" + suffix);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<InventoryItemInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default)
        => await CreateProductAsync(sku, name, unitPrice, null, null, "Unclassified", currency, initialStock, cancellationToken);

    public async Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, decimal? price2, decimal? price3,
        string taxTreatment, string currency, decimal initialStock, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/products");
        request.Content = JsonContent.Create(new { sku, name, unitPrice, price2, price3, taxTreatment, currency, initialStock }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ProductCreateResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty product response.");
        return new InventoryItemInfo(result.Id, result.Sku, result.Name, result.UnitPrice, result.Currency, result.InitialStock, result.HasImage)
        { Price2 = result.Price2, Price3 = result.Price3, TaxTreatment = result.TaxTreatment };
    }

    public async Task SetProductTaxTreatmentAsync(Guid productId, string taxTreatment, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/products/{productId}/tax-treatment");
        request.Content = JsonContent.Create(new { taxTreatment }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UpdateProductAsync(Guid productId, string sku, string name, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/products/{productId}");
        request.Content = JsonContent.Create(new { sku, name }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetProductActiveAsync(Guid productId, bool active, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/products/{productId}/active");
        request.Content = JsonContent.Create(new { active }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<byte[]?> GetProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/products/{productId}/image");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task RemoveProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"api/v1/products/{productId}/image");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/inventory/{productId}/adjustments");
        request.Content = JsonContent.Create(new { quantityDelta, reason }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<StockAdjustmentResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty stock adjustment response.");
        return result.Quantity;
    }

    public async Task<StockMovementPageInfo> GetStockMovementsAsync(Guid productId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/inventory/{productId}/movements?offset={offset}&limit={limit}");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var items = await response.Content.ReadFromJsonAsync<List<StockMovementInfo>>(JsonOptions, cancellationToken) ?? [];
        var hasMore = response.Headers.TryGetValues("X-Has-More", out var values) &&
            bool.TryParse(values.FirstOrDefault(), out var parsed) && parsed;
        return new StockMovementPageInfo(items, hasMore);
    }

    public async Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/customers?query=" + Uri.EscapeDataString(query ?? string.Empty));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<CustomerInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/customers");
        request.Content = JsonContent.Create(new { name, documentNumber, phone, currency, creditLimit }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CustomerInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty customer response.");
    }

    public async Task<CustomerInfo> UpdateCustomerAsync(Guid customerId, string name, string? documentNumber, string? phone, decimal creditLimit, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/customers/{customerId}");
        request.Content = JsonContent.Create(new { name, documentNumber, phone, creditLimit }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CustomerInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty customer response.");
    }

    public async Task DeactivateCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/customers/{customerId}/active");
        request.Content = JsonContent.Create(new { active = false }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ChangePriceAsync(Guid productId, decimal newPrice, string reason, CancellationToken cancellationToken = default)
        => await ChangePriceAsync(productId, "Price1", newPrice, reason, cancellationToken);

    public async Task ChangePriceAsync(Guid productId, string priceTier, decimal newPrice, string reason, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/products/{productId}/price-changes");
        request.Content = JsonContent.Create(new { priceTier, newPrice, reason }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ApplySuggestedPriceAsync(Guid productId, string priceTier, decimal newPrice, decimal? expectedCurrentPrice,
        decimal expectedUnitCost, string reason, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/products/{productId}/price-changes");
        request.Content = JsonContent.Create(new { priceTier, newPrice, reason, expectedCurrentPrice, expectedUnitCost, validateSnapshot = true }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/exchange-rates");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<ExchangeRateInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task SetExchangeRateAsync(string fromCurrency, string toCurrency, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/exchange-rates");
        request.Content = JsonContent.Create(new { fromCurrency, toCurrency, rate, effectiveAtUtc = DateTime.SpecifyKind(effectiveAtUtc, DateTimeKind.Utc), source }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/sales?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SalesReportInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty sales report.");
    }

    public async Task<ProductSalesReportInfo> GetProductSalesReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/products/sales?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}&currency={Uri.EscapeDataString(string.IsNullOrWhiteSpace(currency) ? "Todas" : currency)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProductSalesReportInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió un reporte de ventas por producto vacío.");
    }

    public async Task<SaleDetailReportInfo> GetSalesDetailReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, int page = 1, CancellationToken cancellationToken = default, string? paymentMethod = null, string? operatorName = null, string? status = null, string? query = null)
    {
        var path = $"api/v1/reports/sales/detail?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}&page={Math.Max(1, page)}&pageSize=250&currency={Uri.EscapeDataString(string.IsNullOrWhiteSpace(currency) ? "Todas" : currency)}";
        if (!string.IsNullOrWhiteSpace(paymentMethod)) path += $"&paymentMethod={Uri.EscapeDataString(paymentMethod.Trim())}";
        if (!string.IsNullOrWhiteSpace(operatorName)) path += $"&operatorName={Uri.EscapeDataString(operatorName.Trim())}";
        if (!string.IsNullOrWhiteSpace(status) && status != "Todas") path += $"&status={Uri.EscapeDataString(status.Trim())}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        using var request = CreateRequest(HttpMethod.Get, path); using var response = await SendAuthorizedAsync(request, cancellationToken); await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SaleDetailReportInfo>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("El servicio devolvió un reporte vacío.");
    }

    public async Task<SaleReturnReceipt> ReturnSaleAsync(Guid saleId, SaleReturnInput requestBody, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/sales/{saleId}/returns");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SaleReturnReceipt>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió una respuesta de devolución vacía.");
    }

    public async Task<CashSessionReportInfo> GetCashSessionReportAsync(DateTime fromUtc, DateTime toUtc, string? status, CancellationToken cancellationToken = default)
        => await GetCashSessionReportPageAsync(fromUtc, toUtc, status, 1, 250, cancellationToken);

    public async Task<CashSessionReportInfo> GetCashSessionReportPageAsync(DateTime fromUtc, DateTime toUtc, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/cash-sessions?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}&page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 250)}&status={Uri.EscapeDataString(string.IsNullOrWhiteSpace(status) ? "Todas" : status)}";
        using var request = CreateRequest(HttpMethod.Get, path); using var response = await SendAuthorizedAsync(request, cancellationToken); await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CashSessionReportInfo>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("El servicio devolvió un reporte vacío.");
    }

    public async Task<ReceivableBalanceReportPage> GetReceivableBalanceReportPageAsync(int page = 1, int pageSize = 250,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/receivables/balances?page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 250)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReceivableBalanceReportPage>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió una cartera vacía.");
    }

    public async Task<ReceivableMovementReportPage> GetReceivableMovementReportPageAsync(DateTime fromUtc, DateTime toUtc,
        string? currency, int page = 1, int pageSize = 250, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/receivables/movements?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}" +
                   $"&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}";
        if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency, "Todas", StringComparison.OrdinalIgnoreCase))
            path += $"&currency={Uri.EscapeDataString(currency.Trim().ToUpperInvariant())}";
        path += $"&page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 250)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReceivableMovementReportPage>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió movimientos de cartera vacíos.");
    }

    public async Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/operators");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<OperatorInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/operators");
        request.Content = JsonContent.Create(new { username, pin, role }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetOperatorRoleAsync(Guid operatorId, string role, CancellationToken cancellationToken = default)
        => await SendPatchAsync($"api/v1/operators/{operatorId}/role", new { role }, cancellationToken);

    public async Task SetOperatorActiveAsync(Guid operatorId, bool active, CancellationToken cancellationToken = default)
        => await SendPatchAsync($"api/v1/operators/{operatorId}/active", new { active }, cancellationToken);

    public async Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/audit?limit=200");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<AuditInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<ModuleInstallationClientResult> InstallModuleAsync(string requestedModuleId,
        IReadOnlyList<string> packagePaths, CancellationToken cancellationToken = default)
    {
        using var response = await SendModuleBundleAsync("api/v1/modules/install", requestedModuleId, packagePaths, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModuleInstallationClientResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió una respuesta de instalación vacía.");
    }

    public async Task<ModuleInstallationPlanClientResult> PlanModuleInstallationAsync(string requestedModuleId,
        IReadOnlyList<string> packagePaths, CancellationToken cancellationToken = default)
    {
        using var response = await SendModuleBundleAsync("api/v1/modules/plan", requestedModuleId, packagePaths, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModuleInstallationPlanClientResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió un plan vacío.");
    }

    public async Task<IReadOnlyList<InstalledModuleRecordClient>> GetInstalledModulesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/modules");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<InstalledModuleRecordClient>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<DesktopModuleClientInfo>> GetDesktopModulesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/modules/desktop");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<DesktopModuleClientInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<ModuleManagementAccessInfo> GetModuleManagementAccessAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/modules/management-access");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ModuleManagementAccessInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió un estado vacío de permisos para módulos.");
    }

    public async Task<byte[]> DownloadDesktopModulePackageAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || moduleId.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new ArgumentException("El ID del módulo no es válido.", nameof(moduleId));
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/modules/desktop/{Uri.EscapeDataString(moduleId)}/package");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<TResponse?> GetModuleAsync<TResponse>(string moduleId, string relativeRoute, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildModuleUriPath(moduleId, relativeRoute));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    public async Task<TResponse?> GetModuleAsync<TResponse>(string moduleId, string relativeRoute,
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var queryString = query.Count == 0 ? string.Empty : "?" + string.Join("&", query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
                    throw new ArgumentException("El nombre del parámetro del módulo no es válido.", nameof(query));
                return Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? string.Empty);
            }));
        using var request = CreateRequest(HttpMethod.Get, BuildModuleUriPath(moduleId, relativeRoute) + queryString);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    public async Task<TResponse?> PostModuleAsync<TRequest, TResponse>(string moduleId, string relativeRoute,
        TRequest payload, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, BuildModuleUriPath(moduleId, relativeRoute));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    public async Task PostModuleAsync<TRequest>(string moduleId, string relativeRoute, TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, BuildModuleUriPath(moduleId, relativeRoute));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task PatchModuleAsync<TRequest>(string moduleId, string relativeRoute, TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, BuildModuleUriPath(moduleId, relativeRoute));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task PutModuleAsync<TRequest>(string moduleId, string relativeRoute, TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, BuildModuleUriPath(moduleId, relativeRoute));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static string BuildModuleUriPath(string moduleId, string relativeRoute)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || moduleId.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new ArgumentException("El ID del módulo no es válido.", nameof(moduleId));
        if (string.IsNullOrWhiteSpace(relativeRoute) || relativeRoute.StartsWith('/') || relativeRoute.Contains('\\') || relativeRoute.Contains('?') || relativeRoute.Contains('#'))
            throw new ArgumentException("La ruta del módulo debe ser relativa y segura.", nameof(relativeRoute));
        var segments = relativeRoute.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            throw new ArgumentException("La ruta del módulo contiene un segmento no permitido.", nameof(relativeRoute));
        return $"api/v1/modules/{Uri.EscapeDataString(moduleId)}/{string.Join('/', segments.Select(Uri.EscapeDataString))}";
    }

    public async Task DeactivateModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) throw new ArgumentException("Indica el identificador del módulo.", nameof(moduleId));
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/modules/{Uri.EscapeDataString(moduleId.Trim())}/deactivate");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ActivateModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) throw new ArgumentException("Indica el identificador del módulo.", nameof(moduleId));
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/modules/{Uri.EscapeDataString(moduleId.Trim())}/activate");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<WarehouseClientInfo>> GetWarehousesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/modules/warehouses/warehouses");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<WarehouseClientInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<WarehouseStockClientInfo>> GetWarehouseStockAsync(Guid warehouseId, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var query = includeInactive ? "?includeInactive=true" : string.Empty;
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/modules/warehouses/warehouses/{warehouseId}/stock{query}");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<WarehouseStockClientInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<WarehouseTransferClientInfo>> GetWarehouseTransfersAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/modules/warehouses/transfers");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<WarehouseTransferClientInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task CreateWarehouseAsync(string code, string name, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/modules/warehouses/warehouses");
        request.Content = JsonContent.Create(new { code, name }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetWarehouseActiveAsync(Guid warehouseId, bool active, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/modules/warehouses/warehouses/{warehouseId}/active");
        request.Content = JsonContent.Create(new { active }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task CreateWarehouseTransferAsync(Guid sourceWarehouseId, Guid destinationWarehouseId, IReadOnlyList<WarehouseTransferClientLine> lines, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/modules/warehouses/transfers");
        request.Content = JsonContent.Create(new { sourceWarehouseId, destinationWarehouseId, lines }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DispatchWarehouseTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/modules/warehouses/transfers/{transferId}/dispatch");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ReceiveWarehouseTransferAsync(Guid transferId, IReadOnlyList<WarehouseReceiptClientLine> lines, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/modules/warehouses/transfers/{transferId}/receive");
        request.Content = JsonContent.Create(new { lines }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task AssignTerminalWarehouseAsync(Guid terminalId, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, $"api/v1/modules/warehouses/terminals/{terminalId}/warehouse");
        request.Content = JsonContent.Create(new { warehouseId }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendModuleBundleAsync(string endpoint, string requestedModuleId,
        IReadOnlyList<string> packagePaths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedModuleId)) throw new ArgumentException("Indica el identificador del módulo.", nameof(requestedModuleId));
        ArgumentNullException.ThrowIfNull(packagePaths);
        if (packagePaths.Count is 0 or > 32) throw new ArgumentException("Selecciona entre 1 y 32 paquetes.", nameof(packagePaths));
        if (packagePaths.Any(path => !File.Exists(path))) throw new ArgumentException("Uno de los paquetes seleccionados ya no existe.", nameof(packagePaths));
        if (packagePaths.Sum(path => new FileInfo(path).Length) > 64L * 1024 * 1024)
            throw new ArgumentException("Los paquetes seleccionados superan 64 MiB en conjunto.", nameof(packagePaths));

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(requestedModuleId.Trim()), "requestedModuleId");
        try
        {
            foreach (var path in packagePaths)
            {
                var part = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                part.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(part, "packages", Path.GetFileName(path));
            }
            using var request = CreateRequest(HttpMethod.Post, endpoint);
            request.Content = form;
            form = null!;
            var response = await SendAuthorizedAsync(request, cancellationToken);
            try
            {
                await EnsureSuccessAsync(response, cancellationToken);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        finally { form?.Dispose(); }
    }

    public async Task<IReadOnlyList<SavedReportViewInfo>> GetReportViewsAsync(string reportId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/reports/views?reportId={Uri.EscapeDataString(reportId)}");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<SavedReportViewInfo>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<SavedReportViewInfo> SaveReportViewAsync(string reportId, SavedReportViewDefinition view, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/reports/views");
        request.Content = JsonContent.Create(new { reportId, view }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SavedReportViewInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("El servicio devolvió una vista guardada vacía.");
    }

    public async Task DeleteReportViewAsync(Guid viewId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"api/v1/reports/views/{viewId:D}");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryMovementReportRow>> GetInventoryMovementReportAsync(DateTime fromUtc, DateTime toUtc,
        int page = 1, int pageSize = 250, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/reports/inventory/movements?fromUtc={Uri.EscapeDataString(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("O"))}&page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 250)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<InventoryMovementReportRow>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/reports/inventory");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<InventoryReportRow>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/customers/{customerId}/receivables");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReceivableInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty receivable response.");
    }

    public async Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string? reference, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/customers/{customerId}/payments");
        request.Content = JsonContent.Create(new { amount, currency, reference }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RecordReceivableOpeningBalanceAsync(Guid customerId, decimal amountUsd, string reason, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/customers/{customerId}/receivables/opening-balance");
        request.Content = JsonContent.Create(new { amountUsd, reason, idempotencyKey }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task SendPatchAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Patch, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/products?query=" + Uri.EscapeDataString(query));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var results = await response.Content.ReadFromJsonAsync<List<ProductSearchResponse>>(JsonOptions, cancellationToken) ?? [];
        return results.Select(product => new ProductInfo(product.Id, product.Sku, product.Name, product.UnitPrice,
            product.Currency, product.HasImage, product.Quantity, product.Price2, product.Price3, product.TaxTreatment)).ToArray();
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
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<SaleResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty sale response.");
        return result.SaleId;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_accessToken)) return;
        using var request = CreateRequest(HttpMethod.Post, "api/v1/auth/logout");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) await EnsureSuccessAsync(response, cancellationToken);
        _accessToken = null;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        using var firstRequest = CloneRequest(request, body);
        var response = await _httpClient.SendAsync(firstRequest, cancellationToken);
        if (response.StatusCode != (HttpStatusCode)428) return response;
        PermissionApprovalChallenge? challenge;
        try { challenge = await response.Content.ReadFromJsonAsync<PermissionApprovalChallenge>(JsonOptions, cancellationToken); }
        catch (JsonException) { challenge = null; }
        response.Dispose();
        if (challenge is null || RequestAdministratorPinAsync is null)
            throw new PosApiException((HttpStatusCode)428, "admin_pin_required");
        var credentials = await RequestAdministratorPinAsync(challenge, cancellationToken);
        if (credentials is null) throw new PosApiException(HttpStatusCode.Forbidden, "admin_approval_cancelled");
        using var authorization = CreateRequest(HttpMethod.Post, "api/v1/auth/authorize");
        authorization.Content = JsonContent.Create(new
        {
            administratorUsername = credentials.Username,
            administratorPin = credentials.Pin,
            permission = challenge.Permission,
            operationId = challenge.OperationId,
            operation = challenge.Operation,
            requestFingerprint = challenge.RequestFingerprint
        }, options: JsonOptions);
        using var authorizationResponse = await _httpClient.SendAsync(authorization, cancellationToken);
        await EnsureSuccessAsync(authorizationResponse, cancellationToken);
        var grant = await authorizationResponse.Content.ReadFromJsonAsync<ApprovalResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty administrator approval.");
        using var retry = CloneRequest(request, body);
        retry.Headers.TryAddWithoutValidation("X-Admin-Approval", grant.ApprovalToken);
        return await _httpClient.SendAsync(retry, cancellationToken);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, byte[]? body)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri) { Version = source.Version, VersionPolicy = source.VersionPolicy };
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (source.Content is not null)
                foreach (var header in source.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    public async Task<IReadOnlyList<OperatorPermissionSetting>> GetOperatorPermissionsAsync(Guid operatorId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/operators/{operatorId}/permissions");
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<OperatorPermissionResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The service returned an empty permission matrix.");
        return result.Permissions;
    }

    public async Task SetOperatorPermissionsAsync(Guid operatorId, IReadOnlyList<OperatorPermissionSetting> permissions, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, $"api/v1/operators/{operatorId}/permissions");
        request.Content = JsonContent.Create(new { permissions = permissions.Select(x => new { x.Permission, x.Mode }).ToArray() }, options: JsonOptions);
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, BuildUri(path));
        if (!string.IsNullOrWhiteSpace(_accessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return request;
    }

    private Uri BuildUri(string path) => new(BaseAddress, path);

    private static Uri NormalizeBaseAddress(Uri value)
    {
        if (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The API address must use HTTP or HTTPS.", nameof(value));
        return new Uri(value.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = "service_error";
        string? detail = null;
        string? diagnosticId = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                code = ReadString(document.RootElement, "error") ?? ReadString(document.RootElement, "code") ??
                    ReadString(document.RootElement, "title") ?? code;
                detail = ReadString(document.RootElement, "detail") ?? ReadString(document.RootElement, "message");
                diagnosticId = ReadString(document.RootElement, "diagnosticId");
            }
        }
        catch (JsonException) { }
        throw new PosApiException(response.StatusCode, code, detail, diagnosticId);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public void Dispose() => _httpClient.Dispose();

    private sealed record LoginResponse(Guid OperatorId, Guid TerminalId, DateTime ExpiresAtUtc, string AccessToken);
    private sealed record TerminalPairResponse(string TerminalCredential);
    private sealed record CashSessionResponse(Guid Id, string Currency, decimal OpeningBalance, DateTime OpenedAtUtc,
        decimal OpeningBalanceUsd = 0m, decimal OpeningBalanceVes = 0m, decimal? ExpectedUsd = null, decimal? ExpectedVes = null,
        string? OpenedByOperatorName = null, string? ActiveOperatorName = null, bool IsActiveForCurrentOperator = true);
    private sealed record CashCloseResponse(Guid CashSessionId, decimal ExpectedBalance, decimal CountedBalance, decimal Variance,
        decimal ExpectedUsd = 0m, decimal CountedUsd = 0m, decimal VarianceUsd = 0m, decimal ExpectedVes = 0m, decimal CountedVes = 0m, decimal VarianceVes = 0m);
    private sealed record StockAdjustmentResponse(Guid Id, Guid ProductId, decimal QuantityDelta, decimal Quantity, string? Reason, DateTime OccurredAtUtc);
    private sealed record ProductCreateResponse(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, decimal InitialStock, bool HasImage = false)
    {
        public decimal? Price2 { get; init; }
        public decimal? Price3 { get; init; }
        public string TaxTreatment { get; init; } = "Unclassified";
    }
    private sealed record ProductSearchResponse(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, bool HasImage, decimal Quantity)
    {
        public decimal? Price2 { get; init; }
        public decimal? Price3 { get; init; }
        public string TaxTreatment { get; init; } = "Unclassified";
    }
    private sealed record SaleResponse(Guid SaleId, bool Replayed);
    private sealed record ApprovalResponse(string ApprovalToken);
    private sealed record OperatorPermissionResponse(string Role, List<OperatorPermissionSetting> Permissions);
}
