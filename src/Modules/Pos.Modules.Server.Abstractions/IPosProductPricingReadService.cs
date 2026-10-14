namespace Pos.Modules.Server.Abstractions;

/// <summary>Host-owned read boundary for finance to prepare reviewable price proposals from current catalog costs.</summary>
public interface IPosProductPricingReadService
{
    Task<IReadOnlyList<PosProductPricingSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);
}

public sealed record PosProductPricingSnapshot(Guid ProductId, string Sku, string Name, string Currency,
    decimal? UnitCost, decimal Price1, decimal? Price2, decimal? Price3, bool IsActive);
