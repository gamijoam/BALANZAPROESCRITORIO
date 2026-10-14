using System.Net;
using Pos.Shared.Contracts;

namespace Pos.Desktop.Presentation;

public sealed record ProductInfo(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, bool HasImage = false, decimal? StockQuantity = null,
    decimal? Price2 = null, decimal? Price3 = null, string TaxTreatment = "Unclassified")
{
    public string PriceLabel => $"{Currency} {UnitPrice:N2}";
    public bool IsTaxClassified => TaxTreatment is "Taxable16" or "Taxable8" or "Taxable0" or "Exempt";
    public string TaxTreatmentLabel => TaxTreatment switch
    {
        "Taxable16" => "Gravado 16 %", "Taxable8" => "Gravado 8 %", "Taxable0" => "Gravado 0 %", "Exempt" => "Exento", _ => "Pendiente de clasificar"
    };
}
public sealed record WarehouseClientInfo(Guid Id, string Code, string Name, bool IsActive, bool IsPrimary, decimal StockUnits);
public sealed record WarehouseStockClientInfo(Guid ProductId, string Sku, string Name, decimal Quantity, decimal UnitPrice, string Currency);
public sealed record WarehouseTransferClientInfo(Guid Id, string Status, Guid SourceWarehouseId, string Source, Guid DestinationWarehouseId, string Destination, DateTime CreatedAtUtc, long LineCount, decimal Quantity, decimal Variance)
{
    public string StatusLabel => Status switch
    {
        "Draft" => "Borrador",
        "Dispatched" => "En tránsito",
        "Received" => "Recibido",
        _ => Status
    };
}
public sealed record WarehouseTransferClientLine(Guid ProductId, decimal Quantity);
public sealed record WarehouseReceiptClientLine(Guid ProductId, decimal ReceivedQuantity);
public sealed record WarehouseTransferLineClientInput(Guid ProductId, decimal Quantity);
public sealed record LoginResult(Guid OperatorId, Guid TerminalId, DateTime ExpiresAtUtc, string AccessToken);
public sealed record PermissionApprovalChallenge(string Permission, Guid OperationId, string Operation, string RequestFingerprint);
public sealed record AdministratorCredentials(string Username, string Pin);
public sealed record ModuleInstallationClientResult(string RequestedModuleId, string Status, bool RestartRequired,
    string BackupPath, string BackupSha256, IReadOnlyList<ModuleInstallationClientPackage> Packages);
public sealed record ModuleInstallationClientPackage(string ModuleId, string Version);
public sealed record ModuleInstallationPlanClientResult(string RequestedModuleId, bool PlanValid,
    IReadOnlyList<ModuleInstallationPlanClientEntry> InstallationOrder);
public sealed record ModuleInstallationPlanClientEntry(string Id, string Version, string SignerKeyId,
    string? Entitlement, ModuleClientApiRangeClient ClientApi, IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Migrations, ModuleTerminalCompatibilityClientResult TerminalCompatibility, bool HasRuntimeEntry)
{
    public string RuntimeLabel => HasRuntimeEntry ? "Ejecutable" : "Solo migración";
}
public sealed record ModuleClientApiRangeClient(string Min, string MaxExclusive);
public sealed record DesktopModuleClientInfo(string ModuleId, string Version, string SignerKeyId,
    string SignerPublicKeyPem, IReadOnlyList<string> GrantedPermissions);
public sealed record ModuleManagementAccessInfo(bool CanOpenManagement, bool RequiresAdministratorPin);
public sealed record ModuleTerminalCompatibilityClientResult(bool IsCompatible, int RegisteredTerminalCount,
    int UnreportedTerminalCount, IReadOnlyList<string> IncompatibleVersions)
{
    public string StatusLabel => IsCompatible ? "Compatible" : "Requiere actualización";
    public string IncompatibleVersionsLabel => IncompatibleVersions.Count == 0 ? "—" : string.Join(", ", IncompatibleVersions);
}
public sealed record InstalledModuleRecordClient(string ModuleId, string Version, string SignerKeyId, string Status,
    string BackupSha256, DateTime InstalledAtUtc, string? ActivationDetail = null)
{
    public string StatusLabel => Status switch
    {
        "RestartPending" => "Requiere reinicio",
        "Active" => "Activo",
        "WaitingForTerminals" => "Esperando actualización de terminales",
        "IncompatibleHost" => "Servidor incompatible",
        "NoRuntimeEntry" => "Sin punto de entrada",
        "ActivationFailed" => "No activado",
        "Disabled" => "Desactivado",
        _ => Status
    };
}
public sealed record OperatorPermissionSetting(string Permission, string Mode, string Effective);
public sealed record CashSessionInfo(Guid Id, string Currency, decimal OpeningBalance, DateTime OpenedAtUtc, decimal OpeningBalanceUsd = 0m, decimal OpeningBalanceVes = 0m,
    decimal? ExpectedUsd = null, decimal? ExpectedVes = null, string OpenedByOperatorName = "", string? ActiveOperatorName = null,
    bool IsActiveForCurrentOperator = true);
public sealed record CashCloseInfo(decimal ExpectedBalance, decimal CountedBalance, decimal Variance,
    decimal ExpectedUsd = 0m, decimal CountedUsd = 0m, decimal VarianceUsd = 0m,
    decimal ExpectedVes = 0m, decimal CountedVes = 0m, decimal VarianceVes = 0m);
public sealed record CashMovementInput(decimal Amount, string Direction, string Reason, string Currency = "VES");
public sealed record InventoryItemInfo(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, decimal Quantity, bool HasImage = false)
{
    public string ImageStatusLabel => HasImage ? "Sí" : "No";
    public bool Active { get; init; } = true;
    public string ActiveStatusLabel => Active ? "Activo" : "Inactivo";
    public decimal? Price2 { get; init; }
    public decimal? Price3 { get; init; }
    public string TaxTreatment { get; init; } = "Unclassified";
    public string TaxTreatmentLabel => TaxTreatment switch
    {
        "Taxable16" => "Gravado 16 %", "Taxable8" => "Gravado 8 %", "Taxable0" => "Gravado 0 %", "Exempt" => "Exento", _ => "Pendiente de clasificar"
    };
}
public sealed record StockMovementInfo(Guid Id, decimal QuantityDelta, string Reason, Guid ActorId, Guid TerminalId, DateTime OccurredAtUtc, string? ActorUsername = null)
{
    public DateTime OccurredAtLocal => DateTime.SpecifyKind(OccurredAtUtc, DateTimeKind.Utc).ToLocalTime();
    public string OperatorLabel => string.IsNullOrWhiteSpace(ActorUsername) ? $"Operador {ActorId.ToString("N")[..8]}" : ActorUsername;
}
public sealed record StockMovementPageInfo(IReadOnlyList<StockMovementInfo> Items, bool HasMore);
public sealed record ModuleInventoryMovementInfo(Guid Id, decimal QuantityDelta, string Reason, Guid ActorId,
    Guid TerminalId, DateTime OccurredAtUtc, string? ActorUsername, string Reference, string SourceWarehouse,
    string DestinationWarehouse, string MovementType)
{
    public DateTime OccurredAtLocal => DateTime.SpecifyKind(OccurredAtUtc, DateTimeKind.Utc).ToLocalTime();
    public string OperatorLabel => string.IsNullOrWhiteSpace(ActorUsername) ? $"Operador {ActorId.ToString("N")[..8]}" : ActorUsername;
    public string WarehouseRoute => $"{SourceWarehouse} → {DestinationWarehouse}";
}
public sealed record ModuleInventoryMovementPageInfo(IReadOnlyList<ModuleInventoryMovementInfo> Items, bool HasMore);
public sealed record CustomerInfo(Guid Id, string Name, string? DocumentNumber, string? Phone, string Currency, decimal CreditLimit)
{
    public decimal Balance { get; init; }
    public decimal? BalanceVesEquivalent { get; init; }
    public decimal? AvailableCreditVesEquivalent { get; init; }
    public decimal? CurrentUsdToVesRate { get; init; }
    public decimal AvailableCredit => Math.Max(0m, CreditLimit - Balance);
}
public sealed record ExchangeRateInfo(Guid Id, string FromCurrency, string ToCurrency, decimal Rate, DateTime EffectiveAtUtc, string Source, Guid ActorId);
public sealed record SalesReportRow(DateTime Day, string Currency, int Count, decimal Total);
public sealed record SalesReportInfo(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<SalesReportRow> Sales);
public sealed record SaleDetailReportInfo(int Page, int PageSize, int TotalCount, IReadOnlyList<SaleDetailRow> Sales);
public sealed record SaleDetailRow(Guid Id, DateTime CreatedAtUtc, decimal Total, string Currency, string OperatorName, string CustomerName, Guid CashSessionId, IReadOnlyList<SaleDetailLine> Lines, IReadOnlyList<SaleDetailPayment> Payments)
{
    public string SaleNumber => Id.ToString("N")[..8].ToUpperInvariant();
    public decimal? AppliedUsdToVesRate { get; init; }
    public string HistoricalReferenceLabel
    {
        get
        {
            if (AppliedUsdToVesRate is not > 0m) return Currency == "VES" ? "Ref. USD no disponible" : "Ref. VES no disponible";
            var referenceCurrency = Currency == "VES" ? "USD" : "VES";
            var amount = Currency == "VES" ? Total / AppliedUsdToVesRate.Value : Total * AppliedUsdToVesRate.Value;
            return $"Ref. {referenceCurrency} {PosNumberFormat.FormatAmount(amount)}";
        }
    }
    public string PaymentCurrenciesLabel => Payments
        .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x.Key == Currency ? 0 : 1)
        .Select(x => $"{x.Key} {PosNumberFormat.FormatAmount(x.Sum(p => p.Amount))}")
        .DefaultIfEmpty("Sin pagos")
        .Aggregate((left, right) => $"{left} + {right}");
    public decimal RoundingAdjustment { get; init; }
    public string RoundingAdjustmentLabel => RoundingAdjustment == 0m
        ? string.Empty
        : $"Ajuste de redondeo: {(RoundingAdjustment > 0m ? "+" : "−")}{PosNumberFormat.FormatAmount(decimal.Abs(RoundingAdjustment))} {Currency}";
    public string Status { get; init; } = "Confirmed";
    public string StatusLabel => Status switch
    {
        "Confirmed" => "Confirmada",
        "PartiallyReturned" => "Devuelta parcialmente",
        "Returned" => "Devuelta",
        "Voided" => "Anulada",
        _ => "Estado desconocido"
    };
    public decimal ReturnedAmount { get; init; }
    public decimal NetTotal => Total - ReturnedAmount;
}
public sealed record SaleDetailLine(string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, string OriginalCurrency, decimal OriginalUnitPrice)
{
    public Guid Id { get; init; }
    public string PriceTier { get; init; } = "Price1";
    public string TaxTreatment { get; init; } = "NotCaptured";
    public decimal TaxRate { get; init; }
    public decimal TaxAmount { get; init; }
    public string TaxTreatmentLabel => TaxTreatment switch
    {
        "Taxable16" => "Gravado 16 %", "Taxable8" => "Gravado 8 %", "Taxable0" => "Gravado 0 %", "Exempt" => "Exento", _ => "Impuesto anterior no registrado"
    };
    public decimal ReturnedQuantity { get; init; }
    public decimal ReturnableQuantity { get; init; } = Quantity;
    public bool HasReturnedUnits => ReturnedQuantity > 0m;
    public string ReturnStatusLabel => ReturnedQuantity <= 0m
        ? "Sin devolución"
        : ReturnedQuantity >= Quantity ? "Devuelto completamente" : "Devuelto parcialmente";
}
public sealed record SaleDetailPayment(string Method, decimal Amount, string Currency, decimal? AppliedExchangeRate)
{
    /// <summary>
    /// Describes the tender as received and, when currencies differ, its equivalent
    /// in the sale currency using the exchange rate recorded with the payment.
    /// AppliedExchangeRate is payment-currency units converted to sale-currency units.
    /// </summary>
    public string DescribeForSaleCurrency(string saleCurrency)
    {
        var description = $"{Method}: {Currency} {PosNumberFormat.FormatAmount(Amount)}";
        if (string.Equals(Currency, saleCurrency, StringComparison.OrdinalIgnoreCase)) return description;
        if (AppliedExchangeRate is not > 0m)
            return $"{description} (equivalencia a {saleCurrency} no disponible)";

        var equivalent = Amount * AppliedExchangeRate.Value;
        return $"{description} (equivale a {saleCurrency} {PosNumberFormat.FormatAmount(equivalent)}; tasa {PosNumberFormat.FormatAmount(AppliedExchangeRate.Value)})";
    }
}
public sealed record SaleReturnLineInput(Guid SaleLineId, decimal Quantity, bool Restock);
public sealed record SaleReturnInput(string Reason, IReadOnlyList<SaleReturnLineInput> Lines);
public sealed record SaleReturnReceipt(Guid ReturnId, decimal Amount, string Currency, bool Replayed);
public sealed record CashSessionReportInfo(int Page, int PageSize, int TotalCount, IReadOnlyList<CashSessionReportRow> Sessions);
public sealed record CashSessionReportRow(Guid Id, string Status, string Currency, DateTime OpenedAtUtc, DateTime? ClosedAtUtc, string OperatorName, decimal OpeningUsd, decimal OpeningVes, decimal? ExpectedUsd, decimal? CountedUsd, decimal? VarianceUsd, decimal? ExpectedVes, decimal? CountedVes, decimal? VarianceVes)
{
    public string StatusLabel => Status switch { "Open" => "Abierta", "Closed" => "Cerrada", _ => Status };
    public IReadOnlyList<CashMethodSummary> Payments { get; init; } = [];
    public IReadOnlyList<CashMovementSummary> Movements { get; init; } = [];
}
public sealed record CashMethodSummary(string Operation, string Method, string Currency, decimal Amount);
public sealed record CashMovementSummary(string Direction, string Currency, decimal Amount, string Reason, DateTime OccurredAtUtc)
{
    public string DirectionLabel => Direction switch { "In" => "Entrada", "Out" => "Salida", _ => Direction };
}
public sealed record OperatorInfo(Guid Id, string Username, string Role, bool Active, DateTime CreatedAtUtc)
{
    public string RoleLabel => Role switch { "Cashier" => "Cajero", "Supervisor" => "Supervisor", "Administrator" => "Administrador", _ => "Operador" };
}
public sealed record AuditInfo(Guid Id, Guid ActorId, Guid TerminalId, string Action, string Payload, DateTime OccurredAtUtc);
public sealed record InventoryReportRow(string Currency, int ProductCount, decimal Units, decimal RetailValue);
public sealed record InventoryProductReportRow(string Sku, string Name, decimal Quantity, decimal UnitPrice, string Currency, decimal RetailValue);
public sealed record ProductSalesReportInfo(DateTime FromUtc, DateTime ToUtc, string Currency, IReadOnlyList<ProductSalesReportRow> Products);
public sealed record ProductSalesReportRow(Guid ProductId, string Sku, string Name, string Currency, decimal SoldQuantity, decimal ReturnedQuantity, decimal NetQuantity, decimal GrossAmount, decimal ReturnedAmount, decimal NetAmount);
public sealed record ReceivableEntryInfo(Guid Id, Guid CustomerId, Guid? SaleId, string EntryType, decimal Amount, string Currency, string Reason, Guid ActorId, Guid TerminalId, DateTime OccurredAtUtc)
{
    public decimal? OriginalAmount { get; init; }
    public string? OriginalCurrency { get; init; }
    public decimal? AppliedExchangeRate { get; init; }
    public bool IsOpeningBalance { get; init; }
    public IReadOnlyList<ReceivableSaleItemInfo> SaleItems { get; init; } = [];
}
public sealed record ReceivableSaleItemInfo(string Sku, string Description, decimal Quantity, decimal? AmountUsd);
public sealed record ReceivableInfo(Guid Id, string Name, string Currency, decimal CreditLimit, decimal Balance, IReadOnlyList<ReceivableEntryInfo> Entries)
{
    public decimal? BalanceVesEquivalent { get; init; }
    public decimal? CurrentUsdToVesRate { get; init; }
}
public sealed record SaleLineInput(Guid ProductId, decimal Quantity, decimal? UnitPriceOverride = null, string? PriceOverrideReason = null, string PriceTier = "Price1");
public sealed record SalePaymentInput(string Method, decimal Amount, string Currency);
public sealed record SaleReceipt(Guid SaleId, DateTime IssuedAtUtc, string Currency, decimal Total, IReadOnlyList<ReceiptLine> Lines, IReadOnlyList<SalePaymentInput> Payments)
{
    public decimal RoundingAdjustment { get; init; }
    public string? CustomerName { get; init; }
}
public sealed record ReceiptLine(string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal Total, decimal OriginalUnitPrice, string OriginalCurrency, decimal? AppliedExchangeRate,
    string PriceTier = "Price1", string TaxTreatment = "NotCaptured", decimal TaxRate = 0m, decimal TaxAmount = 0m)
{
    public string TaxTreatmentLabel => TaxTreatment switch
    {
        "Taxable16" => "Gravado 16 %", "Taxable8" => "Gravado 8 %", "Taxable0" => "Gravado 0 %", "Exempt" => "Exento", _ => "Impuesto anterior no registrado"
    };
}

public interface IPosModuleApiClient
{
    Task<TResponse?> GetModuleAsync<TResponse>(string moduleId, string relativeRoute, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta rutas de módulos.");
    Task<TResponse?> GetModuleAsync<TResponse>(string moduleId, string relativeRoute, IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta rutas de módulos con parámetros.");
    Task<TResponse?> PostModuleAsync<TRequest, TResponse>(string moduleId, string relativeRoute, TRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no ejecuta rutas de módulos.");
    Task PostModuleAsync<TRequest>(string moduleId, string relativeRoute, TRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no ejecuta rutas de módulos.");
    Task PatchModuleAsync<TRequest>(string moduleId, string relativeRoute, TRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no ejecuta rutas de módulos.");
    Task PutModuleAsync<TRequest>(string moduleId, string relativeRoute, TRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no ejecuta rutas de módulos.");
}

public interface IPosApiClient : IPosModuleApiClient, IPosReportApiClient
{
    Uri BaseAddress { get; set; }
    string? AccessToken { get; }
    Task<ModuleInstallationPlanClientResult> PlanModuleInstallationAsync(string requestedModuleId, IReadOnlyList<string> packagePaths, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no implementa la planificación de módulos.");
    Task<ModuleInstallationClientResult> InstallModuleAsync(string requestedModuleId, IReadOnlyList<string> packagePaths, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no implementa la instalación de módulos.");
    Task<IReadOnlyList<InstalledModuleRecordClient>> GetInstalledModulesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta los módulos instalados.");
    Task<IReadOnlyList<DesktopModuleClientInfo>> GetDesktopModulesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta los módulos de escritorio activos.");
    Task<ModuleManagementAccessInfo> GetModuleManagementAccessAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta el permiso para administrar módulos.");
    Task<byte[]> DownloadDesktopModulePackageAsync(string moduleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no descarga paquetes de módulos de escritorio.");
    Task DeactivateModuleAsync(string moduleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite desactivar módulos.");
    Task ActivateModuleAsync(string moduleId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite reactivar módulos.");
    Task<IReadOnlyList<WarehouseClientInfo>> GetWarehousesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta almacenes.");
    Task<IReadOnlyList<WarehouseStockClientInfo>> GetWarehouseStockAsync(Guid warehouseId, bool includeInactive = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta existencias por almacén.");
    Task<IReadOnlyList<WarehouseTransferClientInfo>> GetWarehouseTransfersAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta traslados.");
    Task CreateWarehouseAsync(string code, string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite crear almacenes.");
    Task SetWarehouseActiveAsync(Guid warehouseId, bool active, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite cambiar el estado de un almacén.");
    Task CreateWarehouseTransferAsync(Guid sourceWarehouseId, Guid destinationWarehouseId, IReadOnlyList<WarehouseTransferClientLine> lines, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite crear traslados.");
    Task DispatchWarehouseTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite despachar traslados.");
    Task ReceiveWarehouseTransferAsync(Guid transferId, IReadOnlyList<WarehouseReceiptClientLine> lines, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite recibir traslados.");
    Task AssignTerminalWarehouseAsync(Guid terminalId, Guid warehouseId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite asignar terminales a almacenes.");
    Task<LoginResult> LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default);
    Task<CashSessionInfo?> GetCurrentCashSessionAsync(CancellationToken cancellationToken = default);
    Task<CashSessionInfo?> ClaimCurrentCashSessionAsync(CancellationToken cancellationToken = default)
        => GetCurrentCashSessionAsync(cancellationToken);
    Task ReleaseCurrentCashSessionAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default);
    Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalanceUsd, decimal openingBalanceVes, string settlementCurrency, CancellationToken cancellationToken = default)
        => OpenCashSessionAsync(settlementCurrency == "USD" ? openingBalanceUsd : openingBalanceVes, settlementCurrency, cancellationToken);
    Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingBalance, CancellationToken cancellationToken = default);
    Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingUsd, decimal countedClosingVes, CancellationToken cancellationToken = default)
        => CloseCashSessionAsync(cashSessionId, countedClosingUsd, cancellationToken);
    Task RecordCashMovementAsync(Guid cashSessionId, CashMovementInput movement, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(string? query, string status, string currency, string stock, bool? hasImage, CancellationToken cancellationToken = default)
        => GetInventoryAsync(cancellationToken);
    Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default);
    Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, decimal? price2, decimal? price3, string taxTreatment,
        string currency, decimal initialStock, CancellationToken cancellationToken = default)
        => CreateProductAsync(sku, name, unitPrice, currency, initialStock, cancellationToken);
    Task SetProductTaxTreatmentAsync(Guid productId, string taxTreatment, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite clasificar el impuesto del producto.");
    Task UpdateProductAsync(Guid productId, string sku, string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite editar productos.");
    Task SetProductActiveAsync(Guid productId, bool active, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite cambiar el estado del producto.");
    Task UploadProductImageAsync(Guid productId, string imagePath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no admite imágenes de productos.");
    Task<byte[]?> GetProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);
    Task RemoveProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no admite eliminar fotos de productos.");
    Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default);
    Task<StockMovementPageInfo> GetStockMovementsAsync(Guid productId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default);
    Task<Guid> CheckoutAsync(Guid cashSessionId, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<SalePaymentInput> payments, Guid? customerId, string idempotencyKey, Guid? exchangeRateId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query, CancellationToken cancellationToken = default);
    Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default);
    Task<CustomerInfo> UpdateCustomerAsync(Guid customerId, string name, string? documentNumber, string? phone, decimal creditLimit, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite editar clientes.");
    Task DeactivateCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no permite retirar clientes.");
    Task ChangePriceAsync(Guid productId, decimal newPrice, string reason, CancellationToken cancellationToken = default);
    Task ChangePriceAsync(Guid productId, string priceTier, decimal newPrice, string reason, CancellationToken cancellationToken = default)
        => ChangePriceAsync(productId, newPrice, reason, cancellationToken);
    Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default);
    Task SetExchangeRateAsync(string fromCurrency, string toCurrency, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default);
    Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
    Task<ProductSalesReportInfo> GetProductSalesReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProductSalesReportInfo(fromUtc, toUtc, currency ?? "Todas", []));
    Task<SaleDetailReportInfo> GetSalesDetailReportAsync(DateTime fromUtc, DateTime toUtc, string? currency, int page = 1, CancellationToken cancellationToken = default, string? paymentMethod = null, string? operatorName = null, string? status = null, string? query = null)
        => throw new NotSupportedException("El cliente no implementa el reporte de ventas detallado.");
    Task<SaleReturnReceipt> ReturnSaleAsync(Guid saleId, SaleReturnInput request, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no implementa devoluciones.");
    Task<CashSessionReportInfo> GetCashSessionReportAsync(DateTime fromUtc, DateTime toUtc, string? status, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no implementa el reporte de cierres de caja.");
    Task<CashSessionReportInfo> GetCashSessionReportPageAsync(DateTime fromUtc, DateTime toUtc, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
        => GetCashSessionReportAsync(fromUtc, toUtc, status, cancellationToken);
    Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperatorPermissionSetting>> GetOperatorPermissionsAsync(Guid operatorId, CancellationToken cancellationToken = default);
    Task SetOperatorPermissionsAsync(Guid operatorId, IReadOnlyList<OperatorPermissionSetting> permissions, CancellationToken cancellationToken = default);
    Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default);
    Task SetOperatorRoleAsync(Guid operatorId, string role, CancellationToken cancellationToken = default);
    Task SetOperatorActiveAsync(Guid operatorId, bool active, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default);
    Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string? reference, CancellationToken cancellationToken = default);
    Task RecordReceivableOpeningBalanceAsync(Guid customerId, decimal amountUsd, string reason, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no registra saldos iniciales de cuentas por cobrar.");
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

public interface IPosReportApiClient
{
    Task<IReadOnlyList<SavedReportViewInfo>> GetReportViewsAsync(string reportId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta las vistas guardadas de reportes.");
    Task<SavedReportViewInfo> SaveReportViewAsync(string reportId, SavedReportViewDefinition view, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no guarda vistas de reportes.");
    Task DeleteReportViewAsync(Guid viewId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no elimina vistas de reportes.");
    Task<IReadOnlyList<InventoryMovementReportRow>> GetInventoryMovementReportAsync(DateTime fromUtc, DateTime toUtc,
        int page = 1, int pageSize = 250, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta movimientos globales del inventario.");
    Task<ReceivableBalanceReportPage> GetReceivableBalanceReportPageAsync(int page = 1, int pageSize = 250,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta los saldos consolidados de cuentas por cobrar.");
    Task<ReceivableMovementReportPage> GetReceivableMovementReportPageAsync(DateTime fromUtc, DateTime toUtc,
        string? currency, int page = 1, int pageSize = 250, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("El cliente no consulta los movimientos consolidados de cuentas por cobrar.");
}

public sealed class PosApiException(HttpStatusCode statusCode, string code, string? detail = null, string? diagnosticId = null) : Exception(code)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
    public string? DiagnosticId { get; } = diagnosticId;
}
