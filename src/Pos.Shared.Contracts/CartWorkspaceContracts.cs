namespace Pos.Shared.Contracts;

public sealed record CartWorkspaceLineInput(
    Guid ProductId,
    decimal Quantity,
    string PriceTier,
    decimal? UnitPriceOverride = null,
    string? PriceOverrideReason = null,
    Guid? PresentationId = null);

public sealed record CartWorkspaceSaveRequest(
    Guid? WorkspaceId,
    long ExpectedVersion,
    string MutationId,
    Guid? CustomerId,
    IReadOnlyList<CartWorkspaceLineInput> Lines);

public sealed record CartWorkspaceLineSnapshot(
    Guid ProductId,
    string Sku,
    string Name,
    decimal Quantity,
    string PriceTier,
    decimal? UnitPriceOverride,
    string? PriceOverrideReason,
    decimal SavedUnitPrice,
    string SavedCurrency,
    decimal SavedTaxRate,
    decimal SavedLineTotal,
    bool ProductActive,
    decimal CurrentStock,
    decimal CurrentPrice1,
    decimal? CurrentPrice2,
    decimal? CurrentPrice3,
    string CurrentCurrency,
    string CurrentTaxTreatment,
    string? CurrentSku = null,
    string? CurrentName = null,
    Guid? PresentationId = null,
    string PresentationName = "Unidad",
    string PresentationUnitName = "Unidad",
    string PresentationUnitAbbreviation = "ud",
    Guid? PresentationUnitId = null,
    decimal ConversionFactor = 1m,
    decimal InventoryQuantity = 0m,
    decimal CurrentSaleUnitPrice1 = 0m,
    decimal? CurrentSaleUnitPrice2 = null,
    decimal? CurrentSaleUnitPrice3 = null,
    bool SaleUnitAllowsFractionalQuantities = true,
    bool PresentationActive = true,
    string CurrentPresentationName = "Unidad",
    string CurrentPresentationUnitName = "Unidad",
    string CurrentPresentationUnitAbbreviation = "ud",
    Guid? CurrentPresentationUnitId = null,
    decimal CurrentConversionFactor = 1m,
    bool CurrentSaleUnitAllowsFractionalQuantities = true,
    Guid? BaseUnitId = null,
    string BaseUnitName = "Unidad",
    string BaseUnitAbbreviation = "ud",
    bool BaseUnitAllowsFractionalQuantities = true);

public sealed record CartWorkspaceCustomerSnapshot(Guid Id, string Name, string? DocumentNumber,
    string? Phone, string Currency, decimal CreditLimit);

public sealed record CartWorkspaceExchangeRateSnapshot(Guid Id, string FromCurrency, string ToCurrency,
    decimal Rate, DateTime EffectiveAtUtc, string Source, Guid ActorId);

public sealed record CartWorkspaceSnapshot(
    Guid Id,
    long Version,
    string Status,
    Guid? CustomerId,
    string? Note,
    Guid OperatorId,
    string OperatorName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    decimal TotalAtPause,
    IReadOnlyList<CartWorkspaceLineSnapshot> Lines,
    CartWorkspaceCustomerSnapshot? Customer = null,
    string Currency = "VES",
    string PricingFingerprint = "",
    string CurrentSettlementCurrency = "VES",
    CartWorkspaceExchangeRateSnapshot? CurrentExchangeRate = null);

public sealed record CartWorkspacePauseRequest(long ExpectedVersion, string MutationId, string? Note);
public sealed record CartWorkspaceTransitionRequest(long ExpectedVersion, string MutationId, string? ExpectedPricingFingerprint = null);
