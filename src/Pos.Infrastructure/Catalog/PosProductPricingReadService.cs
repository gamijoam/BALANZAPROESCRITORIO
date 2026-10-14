using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Server.Abstractions;

namespace Pos.Infrastructure.Catalog;

public sealed class PosProductPricingReadService(PosDbContext db) : IPosProductPricingReadService
{
    public async Task<IReadOnlyList<PosProductPricingSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count > 500 || productIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("La lista de artículos no es válida.", nameof(productIds));
        if (productIds.Count == 0) return [];

        var ids = productIds.Distinct().ToArray();
        return await db.Products.AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .OrderBy(product => product.Sku)
            .Select(product => new PosProductPricingSnapshot(product.Id, product.Sku, product.Name, product.Currency,
                product.UnitCost, product.UnitPrice, product.Price2, product.Price3, product.Active))
            .ToListAsync(cancellationToken);
    }
}
