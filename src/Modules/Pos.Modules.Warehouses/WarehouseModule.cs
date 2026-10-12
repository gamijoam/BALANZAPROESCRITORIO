using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Domain.Inventory;
using Pos.Modules.Abstractions;

namespace Pos.Modules.Warehouses;

/// <summary>Official optional module for location stock, terminal assignment, and auditable transfers.</summary>
public sealed class WarehouseModule : IPosServerModule
{
    public const string Id = "warehouses";
    private const string MainWarehouseCode = "PRINCIPAL";
    public static readonly Guid MainWarehouseId = new("a0e43e40-69dd-4f00-8a8f-2e9423c0a001");
    private static readonly string Schema = "m_warehouses_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id)))[..12].ToLowerInvariant();
    private static string Table(string name) => $"\"{Schema}\".\"{name}\"";

    public string ModuleId => Id;

    public IReadOnlyList<PosModuleEndpoint> GetEndpoints() =>
    [
        new("GET", "warehouses", ListWarehousesAsync, "Warehouse.View"),
        new("POST", "warehouses", CreateWarehouseAsync, "Warehouse.Manage"),
        new("PATCH", "warehouses/{id:guid}/active", SetWarehouseActiveAsync, "Warehouse.Manage"),
        new("GET", "warehouses/{id:guid}/stock", GetStockAsync, "Warehouse.View"),
        new("PUT", "terminals/{id:guid}/warehouse", AssignTerminalAsync, "Warehouse.Manage"),
        new("GET", "transfers", ListTransfersAsync, "Warehouse.View"),
        new("GET", "transfers/{id:guid}", GetTransferDetailsAsync, "Warehouse.View"),
        new("POST", "transfers", CreateTransferAsync, "Warehouse.Transfer"),
        new("POST", "transfers/{id:guid}/dispatch", DispatchTransferAsync, "Warehouse.Transfer"),
        new("POST", "transfers/{id:guid}/receive", ReceiveTransferAsync, "Warehouse.Transfer")
    ];

    private static async Task<IResult> ListWarehousesAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await EnsureInitializedAsync(connection, null, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT w.id,w.code,w.name,w.is_active,w.is_primary,COALESCE(sum(s.quantity),0)::numeric(18,3) FROM {Table("warehouses")} w LEFT JOIN {Table("warehouse_stock")} s ON s.warehouse_id=w.id GROUP BY w.id ORDER BY w.is_primary DESC,w.name", connection);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<object>();
        while (await reader.ReadAsync(context.RequestAborted))
            rows.Add(new { id = reader.GetGuid(0), code = reader.GetString(1), name = reader.GetString(2), isActive = reader.GetBoolean(3), isPrimary = reader.GetBoolean(4), stockUnits = reader.GetDecimal(5) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateWarehouseAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<CreateWarehouseRequest>(cancellationToken: context.RequestAborted);
        var code = (request?.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (request?.Name ?? string.Empty).Trim();
        if (code.Length is < 2 or > 20 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) || name.Length is < 2 or > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["warehouse"] = ["Indica un código (2–20 caracteres) y nombre (2–100 caracteres) válidos."] });
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        await EnsureInitializedAsync(connection, transaction, context.RequestAborted);
        var id = Guid.NewGuid();
        try
        {
            await using (var command = new NpgsqlCommand($"INSERT INTO {Table("warehouses")} (id,code,name,is_active,is_primary,created_at_utc) VALUES (@id,@code,@name,TRUE,FALSE,@now)", connection, transaction))
            { command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("code", code); command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("now", DateTime.UtcNow); await command.ExecuteNonQueryAsync(context.RequestAborted); }
            await using (var stock = new NpgsqlCommand($"INSERT INTO {Table("warehouse_stock")} (id,warehouse_id,product_id,quantity,version,updated_at_utc) SELECT gen_random_uuid(),@id,s.\"ProductId\",0,0,@now FROM stocks s ON CONFLICT (warehouse_id,product_id) DO NOTHING", connection, transaction))
            { stock.Parameters.AddWithValue("id", id); stock.Parameters.AddWithValue("now", DateTime.UtcNow); await stock.ExecuteNonQueryAsync(context.RequestAborted); }
            await WriteEventAsync(connection, transaction, context, "warehouse.created", new { id, code, name });
            await transaction.CommitAsync(context.RequestAborted);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Results.Conflict(new { error = "warehouse_code_already_exists" }); }
        return Results.Created($"warehouses/{id}", new { id, code, name, isActive = true, isPrimary = false });
    }

    private static async Task<IResult> SetWarehouseActiveAsync(HttpContext context)
    {
        var id = Guid.Parse((string)context.Request.RouteValues["id"]!);
        var request = await context.Request.ReadFromJsonAsync<SetWarehouseActiveRequest>(cancellationToken: context.RequestAborted);
        if (request is null) return Results.BadRequest(new { error = "invalid_request" });
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"UPDATE {Table("warehouses")} SET is_active=@active WHERE id=@id AND is_primary=FALSE AND (@active OR NOT EXISTS (SELECT 1 FROM {Table("warehouse_stock")} s WHERE s.warehouse_id=@id AND s.quantity<>0) AND NOT EXISTS (SELECT 1 FROM {Table("warehouse_transfers")} t WHERE (t.source_warehouse_id=@id OR t.destination_warehouse_id=@id) AND t.status IN ('Draft','Dispatched')))", connection);
        command.Parameters.AddWithValue("active", request.Active); command.Parameters.AddWithValue("id", id);
        var changed = await command.ExecuteNonQueryAsync(context.RequestAborted);
        if (changed != 1) return Results.Conflict(new { error = "warehouse_not_found_or_in_use" });
        await WriteEventAsync(connection, null, context, "warehouse.active_changed", new { id, request.Active });
        return Results.NoContent();
    }

    private static async Task<IResult> GetStockAsync(HttpContext context)
    {
        var id = Guid.Parse((string)context.Request.RouteValues["id"]!);
        var includeInactive = string.Equals(context.Request.Query["includeInactive"], "true", StringComparison.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await EnsureInitializedAsync(connection, null, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT p.\"Id\",p.\"Sku\",p.\"Name\",COALESCE(s.quantity,0)::numeric(18,3),p.\"UnitPrice\",p.\"Currency\" FROM products p LEFT JOIN {Table("warehouse_stock")} s ON s.product_id=p.\"Id\" AND s.warehouse_id=@warehouse WHERE (@includeInactive OR p.\"Active\"=TRUE) ORDER BY p.\"Name\",p.\"Sku\"", connection);
        command.Parameters.AddWithValue("warehouse", id);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<object>();
        while (await reader.ReadAsync(context.RequestAborted))
            rows.Add(new { productId = reader.GetGuid(0), sku = reader.GetString(1), name = reader.GetString(2), quantity = reader.GetDecimal(3), unitPrice = reader.GetDecimal(4), currency = reader.GetString(5) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> AssignTerminalAsync(HttpContext context)
    {
        var terminalId = Guid.Parse((string)context.Request.RouteValues["id"]!);
        var request = await context.Request.ReadFromJsonAsync<AssignTerminalRequest>(cancellationToken: context.RequestAborted);
        if (request is null || request.WarehouseId == Guid.Empty) return Results.BadRequest(new { error = "invalid_request" });
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"INSERT INTO {Table("terminal_warehouses")} (terminal_id,warehouse_id,updated_at_utc) SELECT @terminal,id,@now FROM {Table("warehouses")} WHERE id=@warehouse AND is_active=TRUE ON CONFLICT (terminal_id) DO UPDATE SET warehouse_id=EXCLUDED.warehouse_id,updated_at_utc=EXCLUDED.updated_at_utc", connection);
        command.Parameters.AddWithValue("terminal", terminalId); command.Parameters.AddWithValue("warehouse", request.WarehouseId); command.Parameters.AddWithValue("now", DateTime.UtcNow);
        if (await command.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.NotFound(new { error = "active_warehouse_not_found" });
        await WriteEventAsync(connection, null, context, "terminal.warehouse_assigned", new { terminalId, request.WarehouseId });
        return Results.Ok(new { terminalId, request.WarehouseId });
    }

    private static async Task<IResult> ListTransfersAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT t.id,t.status,t.source_warehouse_id,s.name,t.destination_warehouse_id,d.name,t.created_at_utc,COUNT(l.id),COALESCE(sum(l.quantity),0)::numeric(18,3),COALESCE(sum(l.variance),0)::numeric(18,3) FROM {Table("warehouse_transfers")} t JOIN {Table("warehouses")} s ON s.id=t.source_warehouse_id JOIN {Table("warehouses")} d ON d.id=t.destination_warehouse_id LEFT JOIN {Table("warehouse_transfer_lines")} l ON l.transfer_id=t.id GROUP BY t.id,s.name,d.name ORDER BY t.created_at_utc DESC LIMIT 200", connection);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<object>();
        while (await reader.ReadAsync(context.RequestAborted))
            rows.Add(new { id = reader.GetGuid(0), status = reader.GetString(1), sourceWarehouseId = reader.GetGuid(2), source = reader.GetString(3), destinationWarehouseId = reader.GetGuid(4), destination = reader.GetString(5), createdAtUtc = reader.GetDateTime(6), lineCount = reader.GetInt64(7), quantity = reader.GetDecimal(8), variance = reader.GetDecimal(9) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetTransferDetailsAsync(HttpContext context)
    {
        var transferId = Guid.Parse((string)context.Request.RouteValues["id"]!);
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT t.id,t.status,t.source_warehouse_id,s.name,t.destination_warehouse_id,d.name,t.created_at_utc,l.product_id,l.sku_snapshot,l.name_snapshot,l.quantity,l.received_quantity,l.variance FROM {Table("warehouse_transfers")} t JOIN {Table("warehouses")} s ON s.id=t.source_warehouse_id JOIN {Table("warehouses")} d ON d.id=t.destination_warehouse_id LEFT JOIN {Table("warehouse_transfer_lines")} l ON l.transfer_id=t.id WHERE t.id=@id ORDER BY l.name_snapshot,l.sku_snapshot", connection);
        command.Parameters.AddWithValue("id", transferId);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        if (!await reader.ReadAsync(context.RequestAborted)) return Results.NotFound(new { error = "transfer_not_found" });

        var id = reader.GetGuid(0);
        var status = reader.GetString(1);
        var sourceId = reader.GetGuid(2);
        var source = reader.GetString(3);
        var destinationId = reader.GetGuid(4);
        var destination = reader.GetString(5);
        var createdAtUtc = reader.GetDateTime(6);
        var lines = new List<object>();
        do
        {
            if (reader.IsDBNull(7)) continue;
            lines.Add(new
            {
                productId = reader.GetGuid(7),
                sku = reader.GetString(8),
                name = reader.GetString(9),
                quantity = reader.GetDecimal(10),
                receivedQuantity = reader.IsDBNull(11) ? (decimal?)null : reader.GetDecimal(11),
                variance = reader.GetDecimal(12)
            });
        } while (await reader.ReadAsync(context.RequestAborted));

        return Results.Ok(new { id, status, sourceWarehouseId = sourceId, source, destinationWarehouseId = destinationId,
            destination, createdAtUtc, lines });
    }

    private static async Task<IResult> CreateTransferAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<CreateTransferRequest>(cancellationToken: context.RequestAborted);
        if (request is null || request.SourceWarehouseId == Guid.Empty || request.DestinationWarehouseId == Guid.Empty || request.SourceWarehouseId == request.DestinationWarehouseId || request.Lines is null || request.Lines.Count is < 1 or > 100 || request.Lines.Any(x => x.ProductId == Guid.Empty || x.Quantity <= 0m || decimal.Round(x.Quantity, 3) != x.Quantity) || request.Lines.Select(x => x.ProductId).Distinct().Count() != request.Lines.Count)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["transfer"] = ["El traslado debe incluir almacenes distintos y 1–100 productos únicos con cantidades positivas de hasta tres decimales."] });
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        await EnsureInitializedAsync(connection, transaction, context.RequestAborted);
        if (!await ActiveWarehouseExistsAsync(connection, transaction, request.SourceWarehouseId, context.RequestAborted) || !await ActiveWarehouseExistsAsync(connection, transaction, request.DestinationWarehouseId, context.RequestAborted)) return Results.NotFound(new { error = "active_warehouse_not_found" });
        var products = await LoadProductsAsync(connection, transaction, request.Lines.Select(x => x.ProductId).ToArray(), context.RequestAborted);
        if (products.Count != request.Lines.Count) return Results.BadRequest(new { error = "product_unavailable" });
        var transferId = Guid.NewGuid();
        var actorId = GetOperatorId(context);
        await using (var header = new NpgsqlCommand($"INSERT INTO {Table("warehouse_transfers")} (id,source_warehouse_id,destination_warehouse_id,status,created_by,created_at_utc) VALUES (@id,@source,@destination,'Draft',@actor,@now)", connection, transaction))
        {
            header.Parameters.AddWithValue("id", transferId); header.Parameters.AddWithValue("source", request.SourceWarehouseId); header.Parameters.AddWithValue("destination", request.DestinationWarehouseId); header.Parameters.AddWithValue("actor", actorId); header.Parameters.AddWithValue("now", DateTime.UtcNow); await header.ExecuteNonQueryAsync(context.RequestAborted);
        }
        foreach (var line in request.Lines)
        {
            var product = products[line.ProductId];
            await using var insert = new NpgsqlCommand($"INSERT INTO {Table("warehouse_transfer_lines")} (id,transfer_id,product_id,sku_snapshot,name_snapshot,quantity,received_quantity,variance) VALUES (@id,@transfer,@product,@sku,@name,@quantity,NULL,0)", connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.NewGuid()); insert.Parameters.AddWithValue("transfer", transferId); insert.Parameters.AddWithValue("product", line.ProductId); insert.Parameters.AddWithValue("sku", product.Sku); insert.Parameters.AddWithValue("name", product.Name); insert.Parameters.AddWithValue("quantity", line.Quantity); await insert.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await WriteEventAsync(connection, transaction, context, "transfer.created", new { transferId, request.SourceWarehouseId, request.DestinationWarehouseId, lineCount = request.Lines.Count });
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Created($"transfers/{transferId}", new { id = transferId, status = "Draft" });
    }

    private static async Task<IResult> DispatchTransferAsync(HttpContext context)
    {
        var transferId = Guid.Parse((string)context.Request.RouteValues["id"]!);
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        var transfer = await LoadTransferAsync(connection, transaction, transferId, true, context.RequestAborted);
        if (transfer is null) return Results.NotFound();
        var lines = await LoadTransferLinesAsync(connection, transaction, transferId, context.RequestAborted);
        foreach (var line in lines)
        {
            var effect = WarehouseTransferPolicy.Dispatch(transfer.Status, await ReadStockAsync(connection, transaction, transfer.Source, line.ProductId, context.RequestAborted), line.Quantity);
            if (effect.SourceDelta != 0m && !await ChangeStockAsync(connection, transaction, transfer.Source, line.ProductId, effect.SourceDelta, context.RequestAborted)) return Results.Conflict(new { error = "insufficient_warehouse_stock", productId = line.ProductId });
        }
        await using var update = new NpgsqlCommand($"UPDATE {Table("warehouse_transfers")} SET status='Dispatched',dispatched_by=@actor,dispatched_at_utc=@now WHERE id=@id AND status='Draft'", connection, transaction);
        update.Parameters.AddWithValue("actor", GetOperatorId(context)); update.Parameters.AddWithValue("now", DateTime.UtcNow); update.Parameters.AddWithValue("id", transferId);
        if (await update.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "transfer_state_changed" });
        await WriteEventAsync(connection, transaction, context, "transfer.dispatched", new { transferId, transfer.Source, transfer.Destination });
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { id = transferId, status = "Dispatched" });
    }

    private static async Task<IResult> ReceiveTransferAsync(HttpContext context)
    {
        var transferId = Guid.Parse((string)context.Request.RouteValues["id"]!);
        var request = await context.Request.ReadFromJsonAsync<ReceiveTransferRequest>(cancellationToken: context.RequestAborted);
        if (request is null || request.Lines is { Count: > 100 } || request.Lines?.Any(x => x.ProductId == Guid.Empty || x.ReceivedQuantity < 0m || decimal.Round(x.ReceivedQuantity, 3) != x.ReceivedQuantity) == true || request.Lines is not null && request.Lines.Select(x => x.ProductId).Distinct().Count() != request.Lines.Count)
            return Results.BadRequest(new { error = "invalid_receipt_lines" });
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        var transfer = await LoadTransferAsync(connection, transaction, transferId, true, context.RequestAborted);
        if (transfer is null) return Results.NotFound();
        var lines = await LoadTransferLinesAsync(connection, transaction, transferId, context.RequestAborted);
        var receiptLines = request.Lines is null or { Count: 0 }
            ? lines.Select(line => new ReceiveLineRequest(line.ProductId, line.Quantity)).ToArray()
            : request.Lines;
        if (receiptLines.Count != lines.Count || lines.Any(line => receiptLines.All(received => received.ProductId != line.ProductId))) return Results.BadRequest(new { error = "receipt_must_cover_all_transfer_lines" });
        var variances = new List<object>();
        foreach (var line in lines)
        {
            var received = receiptLines.Single(x => x.ProductId == line.ProductId).ReceivedQuantity;
            var effect = WarehouseTransferPolicy.Receive(transfer.Status, line.Quantity, received);
            await ChangeStockAsync(connection, transaction, transfer.Destination, line.ProductId, effect.DestinationDelta, context.RequestAborted);
            if (effect.Variance < 0m)
            {
                await using var total = new NpgsqlCommand("UPDATE stocks SET \"Quantity\"=\"Quantity\"+@variance,\"Version\"=\"Version\"+1 WHERE \"ProductId\"=@product AND \"Quantity\"+@variance>=0", connection, transaction);
                total.Parameters.AddWithValue("variance", effect.Variance); total.Parameters.AddWithValue("product", line.ProductId);
                if (await total.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "global_stock_variance_conflict", productId = line.ProductId });
            }
            await using var updateLine = new NpgsqlCommand($"UPDATE {Table("warehouse_transfer_lines")} SET received_quantity=@received,variance=@variance WHERE transfer_id=@transfer AND product_id=@product", connection, transaction);
            updateLine.Parameters.AddWithValue("received", received); updateLine.Parameters.AddWithValue("variance", effect.Variance); updateLine.Parameters.AddWithValue("transfer", transferId); updateLine.Parameters.AddWithValue("product", line.ProductId); await updateLine.ExecuteNonQueryAsync(context.RequestAborted);
            if (effect.Variance != 0m) variances.Add(new { productId = line.ProductId, sent = line.Quantity, received, variance = effect.Variance });
        }
        await using var update = new NpgsqlCommand($"UPDATE {Table("warehouse_transfers")} SET status='Received',received_by=@actor,received_at_utc=@now WHERE id=@id AND status='Dispatched'", connection, transaction);
        update.Parameters.AddWithValue("actor", GetOperatorId(context)); update.Parameters.AddWithValue("now", DateTime.UtcNow); update.Parameters.AddWithValue("id", transferId);
        if (await update.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "transfer_state_changed" });
        await WriteEventAsync(connection, transaction, context, "transfer.received", new { transferId, transfer.Source, transfer.Destination, variances });
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { id = transferId, status = "Received", variances });
    }

    private static async Task<NpgsqlConnection> OpenAsync(HttpContext context, CancellationToken ct)
    {
        var connectionString = context.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Pos");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Falta ConnectionStrings:Pos.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task EnsureInitializedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using (var warehouse = new NpgsqlCommand($"INSERT INTO {Table("warehouses")} (id,code,name,is_active,is_primary,created_at_utc) VALUES (@id,@code,'Almacén principal',TRUE,TRUE,@now) ON CONFLICT (id) DO NOTHING", connection, transaction))
        { warehouse.Parameters.AddWithValue("id", MainWarehouseId); warehouse.Parameters.AddWithValue("code", MainWarehouseCode); warehouse.Parameters.AddWithValue("now", DateTime.UtcNow); await warehouse.ExecuteNonQueryAsync(ct); }
        await using var seed = new NpgsqlCommand($"INSERT INTO {Table("warehouse_stock")} (id,warehouse_id,product_id,quantity,version,updated_at_utc) SELECT gen_random_uuid(),@warehouse,s.\"ProductId\",s.\"Quantity\",0,@now FROM stocks s ON CONFLICT (warehouse_id,product_id) DO NOTHING", connection, transaction);
        seed.Parameters.AddWithValue("warehouse", MainWarehouseId); seed.Parameters.AddWithValue("now", DateTime.UtcNow); await seed.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ActiveWarehouseExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    { await using var command = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {Table("warehouses")} WHERE id=@id AND is_active=TRUE)", connection, transaction); command.Parameters.AddWithValue("id", id); return (bool)(await command.ExecuteScalarAsync(ct))!; }

    private static async Task<Dictionary<Guid, ProductSnapshot>> LoadProductsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] ids, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT \"Id\",\"Sku\",\"Name\" FROM products WHERE \"Id\"=ANY(@ids) AND \"Active\"=TRUE", connection, transaction); command.Parameters.AddWithValue("ids", ids);
        await using var reader = await command.ExecuteReaderAsync(ct); var products = new Dictionary<Guid, ProductSnapshot>(); while (await reader.ReadAsync(ct)) products.Add(reader.GetGuid(0), new(reader.GetString(1), reader.GetString(2))); return products;
    }

    private static async Task<TransferSnapshot?> LoadTransferAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT status,source_warehouse_id,destination_warehouse_id FROM {Table("warehouse_transfers")} WHERE id=@id{(forUpdate ? " FOR UPDATE" : "")}", connection, transaction); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)) : null;
    }

    private static async Task<IReadOnlyList<TransferLineSnapshot>> LoadTransferLinesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    { await using var command = new NpgsqlCommand($"SELECT product_id,quantity FROM {Table("warehouse_transfer_lines")} WHERE transfer_id=@id ORDER BY product_id FOR UPDATE", connection, transaction); command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(ct); var lines = new List<TransferLineSnapshot>(); while (await reader.ReadAsync(ct)) lines.Add(new(reader.GetGuid(0), reader.GetDecimal(1))); return lines; }

    private static async Task<decimal> ReadStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid warehouse, Guid product, CancellationToken ct)
    { await using var command = new NpgsqlCommand($"SELECT quantity FROM {Table("warehouse_stock")} WHERE warehouse_id=@warehouse AND product_id=@product FOR UPDATE", connection, transaction); command.Parameters.AddWithValue("warehouse", warehouse); command.Parameters.AddWithValue("product", product); return await command.ExecuteScalarAsync(ct) is decimal value ? value : 0m; }

    private static async Task<bool> ChangeStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid warehouse, Guid product, decimal delta, CancellationToken ct)
    {
        await using (var seed = new NpgsqlCommand($"INSERT INTO {Table("warehouse_stock")} (id,warehouse_id,product_id,quantity,version,updated_at_utc) VALUES (gen_random_uuid(),@warehouse,@product,0,0,@now) ON CONFLICT (warehouse_id,product_id) DO NOTHING", connection, transaction))
        { seed.Parameters.AddWithValue("warehouse", warehouse); seed.Parameters.AddWithValue("product", product); seed.Parameters.AddWithValue("now", DateTime.UtcNow); await seed.ExecuteNonQueryAsync(ct); }
        await using var update = new NpgsqlCommand($"UPDATE {Table("warehouse_stock")} SET quantity=quantity+@delta,version=version+1,updated_at_utc=@now WHERE warehouse_id=@warehouse AND product_id=@product AND quantity+@delta>=0", connection, transaction);
        update.Parameters.AddWithValue("delta", delta); update.Parameters.AddWithValue("now", DateTime.UtcNow); update.Parameters.AddWithValue("warehouse", warehouse); update.Parameters.AddWithValue("product", product); return await update.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task WriteEventAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, HttpContext context, string action, object payload)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {Table("warehouse_events")} (id,actor_id,terminal_id,action,payload,occurred_at_utc) VALUES (@id,@actor,@terminal,@action,@payload,@now)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("actor", GetOperatorId(context)); command.Parameters.AddWithValue("terminal", GetTerminalId(context)); command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("payload", System.Text.Json.JsonSerializer.Serialize(payload)); command.Parameters.AddWithValue("now", DateTime.UtcNow); await command.ExecuteNonQueryAsync(context.RequestAborted);
    }

    private static Guid GetOperatorId(HttpContext context) => context.Items["Pos.OperatorId"] is Guid id ? id : Guid.Empty;
    private static Guid GetTerminalId(HttpContext context) => context.Items["Pos.TerminalId"] is Guid id ? id : Guid.Empty;

    private sealed record ProductSnapshot(string Sku, string Name);
    private sealed record TransferSnapshot(string Status, Guid Source, Guid Destination);
    private sealed record TransferLineSnapshot(Guid ProductId, decimal Quantity);
    public sealed record CreateWarehouseRequest(string? Code, string? Name);
    public sealed record SetWarehouseActiveRequest(bool Active);
    public sealed record AssignTerminalRequest(Guid WarehouseId);
    public sealed record CreateTransferRequest(Guid SourceWarehouseId, Guid DestinationWarehouseId, IReadOnlyList<TransferLineRequest>? Lines);
    public sealed record TransferLineRequest(Guid ProductId, decimal Quantity);
    public sealed record ReceiveTransferRequest(IReadOnlyList<ReceiveLineRequest>? Lines);
    public sealed record ReceiveLineRequest(Guid ProductId, decimal ReceivedQuantity);
}
