using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Modules;
using Pos.Infrastructure.Persistence;
using Pos.Modules.Server.Abstractions;

namespace Pos.Infrastructure.Inventory;

/// <summary>Host-owned, replay-safe bridge from finance receipts into core stock and cost.</summary>
public sealed class PosInventoryReceiptService(PosDbContext db) : IPosInventoryReceiptService
{
    public async Task<bool> ApplyReceiptAsync(Guid receiptId, Guid? warehouseId,
        IReadOnlyList<PosInventoryReceiptLine> lines, Guid actorId, Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty || actorId == Guid.Empty || terminalId == Guid.Empty ||
            lines is null || lines.Count is < 1 or > 100 ||
            lines.Any(line => line.ProductId == Guid.Empty || line.Quantity <= 0m || line.UnitCost < 0m || line.CostCurrency is not ("USD" or "VES")) ||
            lines.Select(line => line.ProductId).Distinct().Count() != lines.Count)
            throw new ArgumentException("Los datos de recepción no son válidos.", nameof(lines));

        var payloadHash = ComputePayloadHash(receiptId, warehouseId, lines);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireReceiptLockAsync(receiptId, cancellationToken);
        var existing = await db.PurchaseReceiptEffects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ReceiptId == receiptId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                throw new InvalidOperationException("purchase_receipt_idempotency_conflict");
            if (existing.ReversedAtUtc.HasValue)
                throw new InvalidOperationException("purchase_receipt_already_reversed");
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var targetWarehouseId = warehouseId ?? await WarehouseStockBridge.GetWarehouseForTerminalAsync(db, terminalId, cancellationToken);
        if (warehouseId.HasValue && targetWarehouseId != warehouseId)
            throw new InvalidOperationException("purchase_warehouse_not_available");
        if (targetWarehouseId.HasValue && !await IsActiveWarehouseAsync(targetWarehouseId.Value, cancellationToken))
            throw new InvalidOperationException("purchase_warehouse_not_available");

        var occurredAt = DateTime.UtcNow;
        var effectLines = new List<PurchaseReceiptEffectLineRecord>(lines.Count);
        foreach (var line in lines.OrderBy(item => item.ProductId))
        {
            var product = await db.Products.FromSqlInterpolated(
                    $"SELECT * FROM products WHERE \"Id\" = {line.ProductId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (product is null || !product.Active) throw new InvalidOperationException("purchase_product_unavailable");
            if (product.Currency != line.CostCurrency) throw new InvalidOperationException("purchase_cost_currency_mismatch");

            var stock = await db.Stocks.FromSqlInterpolated(
                    $"SELECT * FROM stocks WHERE \"ProductId\" = {line.ProductId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (stock is null) throw new InvalidOperationException("purchase_product_stock_missing");

            var priorQuantity = stock.Quantity;
            var priorCost = product.UnitCost;
            var nextCost = InventoryCostPolicy.WeightedAverageUnitCost(priorQuantity, priorCost, line.Quantity, line.UnitCost);
            product.UnitCost = nextCost;
            stock.Quantity += line.Quantity;
            stock.Version++;
            if (targetWarehouseId.HasValue)
                await WarehouseStockBridge.ApplyDeltaAsync(db, targetWarehouseId.Value, line.ProductId, line.Quantity, cancellationToken);
            var movementId = Guid.NewGuid();
            db.StockMovements.Add(new StockMovementRecord
            {
                Id = movementId, ProductId = line.ProductId, QuantityDelta = line.Quantity,
                Reason = $"Recepción de compra {receiptId:N}", ActorId = actorId, TerminalId = terminalId, OccurredAtUtc = occurredAt
            });
            effectLines.Add(new PurchaseReceiptEffectLineRecord
            {
                ReceiptId = receiptId, ProductId = line.ProductId, Quantity = line.Quantity,
                PriorStockQuantity = priorQuantity, PriorUnitCost = priorCost, UnitCostAfter = nextCost,
                QuantityAfterReceipt = stock.Quantity, WarehouseId = targetWarehouseId, MovementId = movementId
            });
        }

        db.PurchaseReceiptEffects.Add(new PurchaseReceiptEffectRecord
        {
            ReceiptId = receiptId, PayloadHash = payloadHash, ActorId = actorId, TerminalId = terminalId, AppliedAtUtc = occurredAt
        });
        db.PurchaseReceiptEffectLines.AddRange(effectLines);
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = actorId, TerminalId = terminalId, Action = "purchase.inventory_received",
            Payload = JsonSerializer.Serialize(new { receiptId, warehouseId = targetWarehouseId, lineCount = lines.Count }),
            OccurredAtUtc = occurredAt
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return false;
    }

    public async Task<bool> ReverseReceiptAsync(Guid receiptId, Guid actorId, Guid terminalId,
        CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty || actorId == Guid.Empty || terminalId == Guid.Empty)
            throw new ArgumentException("Los datos para revertir la recepción no son válidos.", nameof(receiptId));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireReceiptLockAsync(receiptId, cancellationToken);
        var effect = await db.PurchaseReceiptEffects.SingleOrDefaultAsync(item => item.ReceiptId == receiptId, cancellationToken);
        if (effect is null) throw new InvalidOperationException("purchase_receipt_effect_not_found");
        if (effect.ReversedAtUtc.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var lines = await db.PurchaseReceiptEffectLines.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId).OrderBy(item => item.ProductId).ToListAsync(cancellationToken);
        if (lines.Count == 0) throw new InvalidOperationException("purchase_receipt_effect_lines_missing");

        foreach (var line in lines)
        {
            var product = await db.Products.FromSqlInterpolated(
                    $"SELECT * FROM products WHERE \"Id\" = {line.ProductId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            var stock = await db.Stocks.FromSqlInterpolated(
                    $"SELECT * FROM stocks WHERE \"ProductId\" = {line.ProductId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (product is null || stock is null) throw new InvalidOperationException("purchase_receipt_product_missing");

            var hasLaterMovement = await db.StockMovements.AsNoTracking().AnyAsync(movement =>
                movement.ProductId == line.ProductId && movement.Id != line.MovementId && movement.OccurredAtUtc > effect.AppliedAtUtc,
                cancellationToken);
            if (!InventoryCostPolicy.CanReverseLastReceipt(stock.Quantity, line.QuantityAfterReceipt, hasLaterMovement) ||
                product.UnitCost != line.UnitCostAfter)
                throw new InvalidOperationException("purchase_receipt_not_reversible");

            if (line.WarehouseId.HasValue)
                await WarehouseStockBridge.ApplyDeltaAsync(db, line.WarehouseId.Value, line.ProductId, -line.Quantity, cancellationToken);
            stock.Quantity -= line.Quantity;
            stock.Version++;
            product.UnitCost = line.PriorUnitCost;
            db.StockMovements.Add(new StockMovementRecord
            {
                Id = Guid.NewGuid(), ProductId = line.ProductId, QuantityDelta = -line.Quantity,
                Reason = $"Reverso de recepción de compra {receiptId:N}", ActorId = actorId,
                TerminalId = terminalId, OccurredAtUtc = DateTime.UtcNow
            });
        }

        var now = DateTime.UtcNow;
        effect.ReversedAtUtc = now;
        effect.ReversedByActorId = actorId;
        effect.ReversedByTerminalId = terminalId;
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = actorId, TerminalId = terminalId, Action = "purchase.inventory_receipt_reversed",
            Payload = JsonSerializer.Serialize(new { receiptId, lineCount = lines.Count }), OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return false;
    }

    private async Task AcquireReceiptLockAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var bytes = receiptId.ToByteArray();
        await using var command = (NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
        command.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(@key1,@key2)";
        command.Parameters.AddWithValue("key1", BitConverter.ToInt32(bytes, 0));
        command.Parameters.AddWithValue("key2", BitConverter.ToInt32(bytes, 4));
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<bool> IsActiveWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var schema = ModuleMigrationSqlGenerator.SchemaForModule("warehouses");
        var connection = db.Database.GetDbConnection();
        var tableExists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass({0}) IS NOT NULL AS \"Value\"", $"{schema}.warehouse_stock")
            .SingleAsync(cancellationToken);
        if (!tableExists) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{schema}\".\"warehouses\" WHERE id=@id AND is_active=TRUE)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = warehouseId; command.Parameters.Add(parameter);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static string ComputePayloadHash(Guid receiptId, Guid? warehouseId, IReadOnlyList<PosInventoryReceiptLine> lines)
    {
        var canonical = new
        {
            receiptId,
            warehouseId,
            lines = lines.OrderBy(item => item.ProductId).Select(item => new { item.ProductId, item.Quantity, item.UnitCost, item.CostCurrency })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }
}
