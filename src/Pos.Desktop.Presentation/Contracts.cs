using System.Net;

namespace Pos.Desktop.Presentation;

public sealed record ProductInfo(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, bool HasImage = false)
{
    public string PriceLabel => $"{Currency} {UnitPrice:N2}";
}
public sealed record LoginResult(Guid OperatorId, Guid TerminalId, DateTime ExpiresAtUtc, string AccessToken);
public sealed record CashSessionInfo(Guid Id, string Currency, decimal OpeningBalance, DateTime OpenedAtUtc, decimal OpeningBalanceUsd = 0m, decimal OpeningBalanceVes = 0m);
public sealed record CashCloseInfo(decimal ExpectedBalance, decimal CountedBalance, decimal Variance,
    decimal ExpectedUsd = 0m, decimal CountedUsd = 0m, decimal VarianceUsd = 0m,
    decimal ExpectedVes = 0m, decimal CountedVes = 0m, decimal VarianceVes = 0m);
public sealed record CashMovementInput(decimal Amount, string Direction, string Reason, string Currency = "VES");
public sealed record InventoryItemInfo(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, decimal Quantity, bool HasImage = false);
public sealed record StockMovementInfo(Guid Id, decimal QuantityDelta, string Reason, Guid ActorId, DateTime OccurredAtUtc);
public sealed record CustomerInfo(Guid Id, string Name, string? DocumentNumber, string? Phone, string Currency, decimal CreditLimit);
public sealed record ExchangeRateInfo(Guid Id, string FromCurrency, string ToCurrency, decimal Rate, DateTime EffectiveAtUtc, string Source, Guid ActorId);
public sealed record SalesReportRow(DateTime Day, string Currency, int Count, decimal Total);
public sealed record SalesReportInfo(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<SalesReportRow> Sales);
public sealed record SaleDetailReportInfo(int Page, int PageSize, int TotalCount, IReadOnlyList<SaleDetailRow> Sales);
public sealed record SaleDetailRow(Guid Id, DateTime CreatedAtUtc, decimal Total, string Currency, string OperatorName, string CustomerName, Guid CashSessionId, IReadOnlyList<SaleDetailLine> Lines, IReadOnlyList<SaleDetailPayment> Payments);
public sealed record SaleDetailLine(string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, string OriginalCurrency, decimal OriginalUnitPrice);
public sealed record SaleDetailPayment(string Method, decimal Amount, string Currency, decimal? AppliedExchangeRate);
public sealed record CashSessionReportInfo(int Page, int PageSize, int TotalCount, IReadOnlyList<CashSessionReportRow> Sessions);
public sealed record CashSessionReportRow(Guid Id, string Status, string Currency, DateTime OpenedAtUtc, DateTime? ClosedAtUtc, string OperatorName, decimal OpeningUsd, decimal OpeningVes, decimal? ExpectedUsd, decimal? CountedUsd, decimal? VarianceUsd, decimal? ExpectedVes, decimal? CountedVes, decimal? VarianceVes);
public sealed record OperatorInfo(Guid Id, string Username, string Role, bool Active, DateTime CreatedAtUtc)
{
    public string RoleLabel => Role switch { "Cashier" => "Cajero", "Supervisor" => "Supervisor", "Administrator" => "Administrador", _ => "Operador" };
}
public sealed record AuditInfo(Guid Id, Guid ActorId, Guid TerminalId, string Action, string Payload, DateTime OccurredAtUtc);
public sealed record InventoryReportRow(string Currency, int ProductCount, decimal Units, decimal RetailValue);
public sealed record InventoryProductReportRow(string Sku, string Name, decimal Quantity, decimal UnitPrice, string Currency, decimal RetailValue);
public sealed record ReceivableEntryInfo(Guid Id, Guid CustomerId, Guid? SaleId, string EntryType, decimal Amount, string Currency, string Reason, Guid ActorId, Guid TerminalId, DateTime OccurredAtUtc);
public sealed record ReceivableInfo(Guid Id, string Name, string Currency, decimal CreditLimit, decimal Balance, IReadOnlyList<ReceivableEntryInfo> Entries);
public sealed record SaleLineInput(Guid ProductId, decimal Quantity);
public sealed record SalePaymentInput(string Method, decimal Amount, string Currency);
public sealed record SaleReceipt(Guid SaleId, DateTime IssuedAtUtc, string Currency, decimal Total, IReadOnlyList<ReceiptLine> Lines, IReadOnlyList<SalePaymentInput> Payments);
public sealed record ReceiptLine(string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal Total, decimal OriginalUnitPrice, string OriginalCurrency, decimal? AppliedExchangeRate);

public interface IPosApiClient
{
    Uri BaseAddress { get; set; }
    string? AccessToken { get; }
    Task<LoginResult> LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default);
    Task<CashSessionInfo?> GetCurrentCashSessionAsync(CancellationToken cancellationToken = default);
    Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default);
    Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalanceUsd, decimal openingBalanceVes, string settlementCurrency, CancellationToken cancellationToken = default)
        => OpenCashSessionAsync(settlementCurrency == "USD" ? openingBalanceUsd : openingBalanceVes, settlementCurrency, cancellationToken);
    Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingBalance, CancellationToken cancellationToken = default);
    Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingUsd, decimal countedClosingVes, CancellationToken cancellationToken = default)
        => CloseCashSessionAsync(cashSessionId, countedClosingUsd, cancellationToken);
    Task RecordCashMovementAsync(Guid cashSessionId, CashMovementInput movement, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default);
    Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default);
    Task UploadProductImageAsync(Guid productId, string imagePath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no admite imágenes de productos.");
    Task<byte[]?> GetProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);
    Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockMovementInfo>> GetStockMovementsAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default);
    Task<Guid> CheckoutAsync(Guid cashSessionId, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<SalePaymentInput> payments, Guid? customerId, string idempotencyKey, Guid? exchangeRateId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query, CancellationToken cancellationToken = default);
    Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default);
    Task ChangePriceAsync(Guid productId, decimal newPrice, string reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default);
    Task SetExchangeRateAsync(string fromCurrency, string toCurrency, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default);
    Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
    Task<SaleDetailReportInfo> GetSalesDetailReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, int page = 1, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no implementa el reporte de ventas detallado.");
    Task<CashSessionReportInfo> GetCashSessionReportAsync(DateTime fromUtc, DateTime toUtc, string? status, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no implementa el reporte de cierres de caja.");
    Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default);
    Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default);
    Task SetOperatorRoleAsync(Guid operatorId, string role, CancellationToken cancellationToken = default);
    Task SetOperatorActiveAsync(Guid operatorId, bool active, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default);
    Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string reason, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

public sealed class PosApiException(HttpStatusCode statusCode, string code) : Exception(code)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
