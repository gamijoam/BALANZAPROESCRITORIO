using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Modules;

/// <summary>
/// Keeps the base POS stock total and optional warehouse balances in one PostgreSQL transaction.
/// It is intentionally tolerant of an uninstalled module: the base POS keeps its original behavior.
/// </summary>
public static class WarehouseStockBridge
{
    public const string ModuleId = "warehouses";
    public static readonly Guid MainWarehouseId = new("a0e43e40-69dd-4f00-8a8f-2e9423c0a001");
    private static readonly string Schema = ModuleMigrationSqlGenerator.SchemaForModule(ModuleId);
    private static string Q(string table) => $"\"{Schema}\".\"{table}\"";

    public static async Task<Guid?> GetWarehouseForTerminalAsync(PosDbContext db, Guid terminalId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (!await IsInstalledAsync(connection, db.Database.CurrentTransaction?.GetDbTransaction(), ct)) return null;
        await EnsureInitializedAsync(connection, db.Database.CurrentTransaction?.GetDbTransaction(), ct);
        await using var command = Command(connection, db.Database.CurrentTransaction?.GetDbTransaction(),
            $"SELECT COALESCE((SELECT warehouse_id FROM {Q("terminal_warehouses")} WHERE terminal_id = @terminal), @main)");
        command.Parameters.AddWithValue("terminal", terminalId);
        command.Parameters.AddWithValue("main", MainWarehouseId);
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Returns the warehouse-specific quantities for a terminal. A null result means the
    /// warehouse module has never been installed, so callers should use the legacy global stock.
    /// Missing product rows in an installed module intentionally resolve to zero.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, decimal>?> GetStockForTerminalAsync(
        PosDbContext db, Guid terminalId, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count == 0) return new Dictionary<Guid, decimal>();

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            if (!await IsInstalledAsync(connection, transaction, ct)) return null;

            await EnsureInitializedAsync(connection, transaction, ct);
            await using var warehouseCommand = Command(connection, transaction,
                $"SELECT COALESCE((SELECT warehouse_id FROM {Q("terminal_warehouses")} WHERE terminal_id = @terminal), @main)");
            warehouseCommand.Parameters.AddWithValue("terminal", terminalId);
            warehouseCommand.Parameters.AddWithValue("main", MainWarehouseId);
            var warehouseId = (Guid)(await warehouseCommand.ExecuteScalarAsync(ct))!;

            var quantities = new Dictionary<Guid, decimal>();
            await using var stockCommand = Command(connection, transaction,
                $"SELECT product_id, quantity FROM {Q("warehouse_stock")} WHERE warehouse_id = @warehouse AND product_id = ANY(@products)");
            stockCommand.Parameters.AddWithValue("warehouse", warehouseId);
            stockCommand.Parameters.AddWithValue("products", productIds.Distinct().ToArray());
            await using var reader = await stockCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) quantities[reader.GetGuid(0)] = reader.GetDecimal(1);
            return quantities;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    public static async Task RecordSaleAsync(PosDbContext db, Guid saleId, Guid warehouseId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (!await IsInstalledAsync(connection, transaction, ct)) return;
        await using var command = Command(connection, transaction,
            $"INSERT INTO {Q("sale_warehouses")} (sale_id, warehouse_id) VALUES (@sale, @warehouse) ON CONFLICT (sale_id) DO NOTHING");
        command.Parameters.AddWithValue("sale", saleId);
        command.Parameters.AddWithValue("warehouse", warehouseId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> ConsumeAsync(PosDbContext db, Guid warehouseId, Guid productId, decimal quantity, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (!await IsInstalledAsync(connection, transaction, ct)) return true;
        await using var command = Command(connection, transaction,
            $"UPDATE {Q("warehouse_stock")} SET quantity = quantity - @quantity, version = version + 1, updated_at_utc = @now WHERE warehouse_id = @warehouse AND product_id = @product AND quantity >= @quantity");
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue("warehouse", warehouseId);
        command.Parameters.AddWithValue("product", productId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public static async Task ApplyDeltaAsync(PosDbContext db, Guid warehouseId, Guid productId, decimal delta, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (!await IsInstalledAsync(connection, transaction, ct)) return;
        await using (var insert = Command(connection, transaction,
            $"INSERT INTO {Q("warehouse_stock")} (id, warehouse_id, product_id, quantity, version, updated_at_utc) VALUES (@id, @warehouse, @product, 0, 0, @now) ON CONFLICT (warehouse_id, product_id) DO NOTHING"))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("warehouse", warehouseId);
            insert.Parameters.AddWithValue("product", productId);
            insert.Parameters.AddWithValue("now", DateTime.UtcNow);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var command = Command(connection, transaction,
            $"UPDATE {Q("warehouse_stock")} SET quantity = quantity + @delta, version = version + 1, updated_at_utc = @now WHERE warehouse_id = @warehouse AND product_id = @product AND quantity + @delta >= 0");
        command.Parameters.AddWithValue("delta", delta);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue("warehouse", warehouseId);
        command.Parameters.AddWithValue("product", productId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("La existencia por almacén no permite aplicar el movimiento solicitado.");
    }

    public static async Task AddProductToWarehousesAsync(PosDbContext db, Guid productId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (!await IsInstalledAsync(connection, transaction, ct)) return;
        await EnsureInitializedAsync(connection, transaction, ct);
        await using var command = Command(connection, transaction,
            $"INSERT INTO {Q("warehouse_stock")} (id, warehouse_id, product_id, quantity, version, updated_at_utc) SELECT gen_random_uuid(), w.id, s.\"ProductId\", CASE WHEN w.is_primary THEN s.\"Quantity\" ELSE 0 END, 0, @now FROM {Q("warehouses")} w CROSS JOIN stocks s WHERE s.\"ProductId\" = @product ON CONFLICT (warehouse_id, product_id) DO NOTHING");
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue("product", productId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task RestockSaleAsync(PosDbContext db, Guid saleId, Guid productId, decimal quantity, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (!await IsInstalledAsync(connection, transaction, ct)) return;
        await using var find = Command(connection, transaction,
            $"SELECT warehouse_id FROM {Q("sale_warehouses")} WHERE sale_id = @sale");
        find.Parameters.AddWithValue("sale", saleId);
        var value = await find.ExecuteScalarAsync(ct);
        var warehouseId = value is Guid id ? id : MainWarehouseId;
        await using var insert = Command(connection, transaction,
            $"INSERT INTO {Q("warehouse_stock")} (id, warehouse_id, product_id, quantity, version, updated_at_utc) VALUES (@id, @warehouse, @product, @quantity, 1, @now) ON CONFLICT (warehouse_id, product_id) DO UPDATE SET quantity = {Q("warehouse_stock")}.quantity + EXCLUDED.quantity, version = {Q("warehouse_stock")}.version + 1, updated_at_utc = EXCLUDED.updated_at_utc");
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("warehouse", warehouseId);
        insert.Parameters.AddWithValue("product", productId);
        insert.Parameters.AddWithValue("quantity", quantity);
        insert.Parameters.AddWithValue("now", DateTime.UtcNow);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsInstalledAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = Command(connection, transaction, "SELECT to_regclass(@table) IS NOT NULL");
            command.Parameters.AddWithValue("table", $"{Schema}.warehouse_stock");
            return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private static async Task EnsureInitializedAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using (var warehouse = Command(connection, transaction,
            $"INSERT INTO {Q("warehouses")} (id, code, name, is_active, is_primary, created_at_utc) VALUES (@id, 'PRINCIPAL', 'Almacén principal', TRUE, TRUE, @now) ON CONFLICT (id) DO NOTHING"))
        {
            warehouse.Parameters.AddWithValue("id", MainWarehouseId);
            warehouse.Parameters.AddWithValue("now", DateTime.UtcNow);
            await warehouse.ExecuteNonQueryAsync(ct);
        }
        await using var seed = Command(connection, transaction,
            $"INSERT INTO {Q("warehouse_stock")} (id, warehouse_id, product_id, quantity, version, updated_at_utc) SELECT gen_random_uuid(), @warehouse, s.\"ProductId\", s.\"Quantity\", 0, @now FROM stocks s ON CONFLICT (warehouse_id, product_id) DO NOTHING");
        seed.Parameters.AddWithValue("warehouse", MainWarehouseId);
        seed.Parameters.AddWithValue("now", DateTime.UtcNow);
        await seed.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(DbConnection connection, DbTransaction? transaction, string sql) =>
        new(sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
}
