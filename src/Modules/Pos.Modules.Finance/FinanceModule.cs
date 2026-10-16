using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Modules.Abstractions;
using Pos.Modules.Server.Abstractions;

namespace Pos.Modules.Finance;

/// <summary>Server-authoritative supplier, purchase invoice, and accounts-payable endpoints.</summary>
public sealed class FinanceModule : IPosServerModule
{
    public const string Id = "finance";
    private static readonly string Schema = "m_finance_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id)))[..12].ToLowerInvariant();
    private static string Table(string name) => $"\"{Schema}\".\"{name}\"";
    public string ModuleId => Id;

    public IReadOnlyList<PosModuleEndpoint> GetEndpoints() =>
    [
        new("GET", "suppliers", ListSuppliersAsync, "Supplier.View"),
        new("POST", "suppliers", CreateSupplierAsync, "Supplier.Manage"),
        new("PATCH", "suppliers/{id:guid}/active", SetSupplierActiveAsync, "Supplier.Manage"),
        new("GET", "purchases", ListPurchasesAsync, "Purchase.View"),
        new("GET", "purchases/{id:guid}", GetPurchaseAsync, "Purchase.View"),
        new("POST", "purchases", CreatePurchaseDraftAsync, "Purchase.Manage"),
        new("PUT", "purchases/{id:guid}", UpdatePurchaseDraftAsync, "Purchase.Manage"),
        new("POST", "purchases/{id:guid}/post", PostPurchaseAsync, "Purchase.Manage"),
        new("POST", "purchases/{id:guid}/post-and-pay", PostImmediatePurchaseAsync, "Purchase.Manage", "SupplierPayment.Manage"),
        new("GET", "purchases/{id:guid}/price-suggestions", GetPriceSuggestionsAsync, "Purchase.View", "PriceSuggestion.Manage"),
        new("GET", "price-suggestions/margins", GetPriceSuggestionMarginsAsync, "PriceSuggestion.Manage"),
        new("PUT", "price-suggestions/margins", SetPriceSuggestionMarginsAsync, "PriceSuggestion.Manage"),
        new("GET", "purchases/{id:guid}/receipts", ListReceiptsAsync, "Purchase.View"),
        new("POST", "purchases/{id:guid}/receipts", ReceivePurchaseAsync, "Purchase.Manage"),
        new("POST", "purchases/{id:guid}/receipts/{receiptId:guid}/reverse", ReverseReceiptAsync, "Purchase.Manage"),
        new("POST", "purchases/{id:guid}/reverse", ReversePurchaseAsync, "Purchase.Manage"),
        new("GET", "payables", ListPayablesAsync, "Purchase.View"),
        new("GET", "purchases/{id:guid}/payments", ListPaymentsAsync, "Purchase.View"),
        new("POST", "purchases/{id:guid}/payments", CreatePaymentAsync, "SupplierPayment.Manage"),
        new("POST", "payments/{id:guid}/reverse", ReversePaymentAsync, "SupplierPayment.Manage"),
        new("GET", "summary", GetFinanceSummaryAsync, "Finance.Reports")
    ];

    private static async Task<IResult> ListSuppliersAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT id,code,legal_name,tax_id,phone,email,address,is_active,created_at_utc,updated_at_utc FROM {Table("suppliers")} ORDER BY legal_name,code LIMIT 1000", connection);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<SupplierInfo>();
        while (await reader.ReadAsync(context.RequestAborted)) rows.Add(ReadSupplier(reader));
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateSupplierAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<SupplierRequest>(cancellationToken: context.RequestAborted);
        var code = (request?.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (request?.LegalName ?? string.Empty).Trim();
        if (code.Length is < 2 or > 32 || code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) ||
            name.Length is < 2 or > 160 || HasInvalidLength(request?.TaxId, 80) || HasInvalidLength(request?.Phone, 64) ||
            HasInvalidLength(request?.Email, 160) || HasInvalidLength(request?.Address, 300))
            return ValidationError("supplier", "Indica código y razón social válidos; los datos de contacto no deben superar los límites indicados.");

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        try
        {
            await using var command = new NpgsqlCommand($"INSERT INTO {Table("suppliers")} (id,code,legal_name,tax_id,phone,email,address,is_active,created_by,created_at_utc,updated_at_utc) VALUES (@id,@code,@name,@tax,@phone,@email,@address,TRUE,@actor,@now,@now)", connection, transaction);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("code", code); command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("tax", (object?)TrimOrNull(request!.TaxId) ?? DBNull.Value); command.Parameters.AddWithValue("phone", (object?)TrimOrNull(request.Phone) ?? DBNull.Value);
            command.Parameters.AddWithValue("email", (object?)TrimOrNull(request.Email) ?? DBNull.Value); command.Parameters.AddWithValue("address", (object?)TrimOrNull(request.Address) ?? DBNull.Value);
            command.Parameters.AddWithValue("actor", GetActorId(context)); command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(context.RequestAborted);
            await WriteEventAsync(connection, transaction, context, "supplier.created", new { id, code, name }, now);
            await transaction.CommitAsync(context.RequestAborted);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Results.Conflict(new { error = "supplier_code_already_exists" }); }
        return Results.Created($"suppliers/{id:D}", new SupplierInfo(id, code, name, TrimOrNull(request!.TaxId), TrimOrNull(request.Phone), TrimOrNull(request.Email), TrimOrNull(request.Address), true, now, now));
    }

    private static async Task<IResult> SetSupplierActiveAsync(HttpContext context)
    {
        var id = RouteGuid(context, "id");
        var request = await context.Request.ReadFromJsonAsync<SetActiveRequest>(cancellationToken: context.RequestAborted);
        if (request is null || id == Guid.Empty) return Results.BadRequest(new { error = "invalid_request" });
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        await using var command = new NpgsqlCommand($"UPDATE {Table("suppliers")} SET is_active=@active,updated_at_utc=@now WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("active", request.Active); command.Parameters.AddWithValue("now", DateTime.UtcNow); command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.NotFound(new { error = "supplier_not_found" });
        await WriteEventAsync(connection, transaction, context, "supplier.active_changed", new { id, request.Active }, DateTime.UtcNow);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ListPurchasesAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"""
            SELECT i.id,i.supplier_id,s.legal_name,i.document_type,i.document_number,i.control_number,i.currency,
                   i.issue_date,i.due_date,i.total,i.status,i.payment_term,
                   COALESCE((SELECT sum(p.amount) FROM {Table("supplier_payables")} p WHERE p.invoice_id=i.id),0)::numeric(18,4)
            FROM {Table("purchase_invoices")} i JOIN {Table("suppliers")} s ON s.id=i.supplier_id
            ORDER BY i.issue_date DESC,i.created_at_utc DESC LIMIT 500
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<PurchaseSummary>();
        while (await reader.ReadAsync(context.RequestAborted))
            rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), NullableString(reader, 5),
                reader.GetString(6), DateOnly.FromDateTime(reader.GetDateTime(7)), reader.IsDBNull(8) ? null : DateOnly.FromDateTime(reader.GetDateTime(8)),
                reader.GetDecimal(9), reader.GetString(10), reader.GetString(11), reader.GetDecimal(12)));
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetPurchaseAsync(HttpContext context)
    {
        var id = RouteGuid(context, "id");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        var header = await ReadPurchaseHeaderAsync(connection, null, id, context.RequestAborted);
        if (header is null) return Results.NotFound(new { error = "purchase_not_found" });
        await using var command = new NpgsqlCommand($"SELECT id,product_id,sku_snapshot,name_snapshot,quantity,received_quantity,unit_price,discount,tax,tax_recoverable,line_total FROM {Table("purchase_invoice_lines")} WHERE invoice_id=@id ORDER BY name_snapshot,sku_snapshot", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var lines = new List<PurchaseLineInfo>();
        while (await reader.ReadAsync(context.RequestAborted))
            lines.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetBoolean(9), reader.GetDecimal(10)));
        return Results.Ok(new PurchaseDetail(header, lines));
    }

    private static Task<IResult> CreatePurchaseDraftAsync(HttpContext context)
        => SavePurchaseDraftAsync(context, null);

    private static Task<IResult> UpdatePurchaseDraftAsync(HttpContext context)
        => SavePurchaseDraftAsync(context, RouteGuid(context, "id"));

    private static async Task<IResult> SavePurchaseDraftAsync(HttpContext context, Guid? purchaseId)
    {
        var request = await context.Request.ReadFromJsonAsync<PurchaseDraftRequest>(cancellationToken: context.RequestAborted);
        if (request is null) return ValidationError("purchase", "La factura no es válida.");
        if (!ValidatePurchaseDraft(request, out var validationMessage)) return ValidationError("purchase", validationMessage ?? "La factura no es válida.");
        var idempotencyKey = (request.IdempotencyKey ?? string.Empty).Trim();
        if (purchaseId is null && (idempotencyKey.Length is < 8 or > 128 || idempotencyKey.Any(char.IsControl)))
            return ValidationError("idempotencyKey", "El borrador requiere una clave de operación válida.");
        var requestHash = HashDraft(request);
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        if (purchaseId is null)
        {
            await using var replay = new NpgsqlCommand($"SELECT id,idempotency_hash FROM {Table("purchase_invoices")} WHERE idempotency_key=@key", connection, transaction);
            replay.Parameters.AddWithValue("key", idempotencyKey);
            await using var replayReader = await replay.ExecuteReaderAsync(context.RequestAborted);
            if (await replayReader.ReadAsync(context.RequestAborted))
            {
                var existingId = replayReader.GetGuid(0); var existingHash = replayReader.GetString(1);
                await replayReader.CloseAsync();
                await transaction.CommitAsync(context.RequestAborted);
                return string.Equals(existingHash, requestHash, StringComparison.Ordinal)
                    ? Results.Ok(new { id = existingId, status = "Draft", replayed = true })
                    : Results.Conflict(new { error = "purchase_idempotency_conflict" });
            }
        }

        if (!await IsActiveSupplierAsync(connection, transaction, request.SupplierId, context.RequestAborted))
            return Results.Conflict(new { error = "active_supplier_required" });
        var productSnapshots = await LoadProductsAsync(connection, transaction, request.Lines.Select(line => line.ProductId).ToArray(), context.RequestAborted);
        if (productSnapshots.Count != request.Lines.Count) return Results.Conflict(new { error = "purchase_product_unavailable" });

        var id = purchaseId ?? Guid.NewGuid();
        if (purchaseId is Guid existingPurchaseId)
        {
            var status = await ReadInvoiceStatusAsync(connection, transaction, existingPurchaseId, true, context.RequestAborted);
            if (status is null) return Results.NotFound(new { error = "purchase_not_found" });
            if (status != "Draft") return Results.Conflict(new { error = "purchase_not_editable" });
            await using var deleteLines = new NpgsqlCommand($"DELETE FROM {Table("purchase_invoice_lines")} WHERE invoice_id=@id", connection, transaction);
            deleteLines.Parameters.AddWithValue("id", id); await deleteLines.ExecuteNonQueryAsync(context.RequestAborted);
            await using var update = new NpgsqlCommand($"""
                UPDATE {Table("purchase_invoices")} SET supplier_id=@supplier,document_type=@type,document_number=@number,
                control_number=@control,currency=@currency,exchange_rate=@rate,issue_date=@issue,due_date=@due,subtotal=@subtotal,
                discount=@discount,freight=@freight,tax=@tax,total=@total,payment_term=@term,notes=@notes,updated_at_utc=@now
                WHERE id=@id AND status='Draft'
                """, connection, transaction);
            AddPurchaseHeaderParameters(update, id, request, idempotencyKey, requestHash, context);
            await update.ExecuteNonQueryAsync(context.RequestAborted);
        }
        else
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {Table("purchase_invoices")} (id,supplier_id,document_type,document_number,control_number,currency,
                exchange_rate,issue_date,due_date,subtotal,discount,freight,tax,total,paid_amount,payment_term,status,notes,
                idempotency_key,idempotency_hash,created_by,created_at_utc,updated_at_utc)
                VALUES (@id,@supplier,@type,@number,@control,@currency,@rate,@issue,@due,@subtotal,@discount,@freight,@tax,@total,
                0,@term,'Draft',@notes,@key,@hash,@actor,@now,@now)
                """, connection, transaction);
            AddPurchaseHeaderParameters(insert, id, request, idempotencyKey, requestHash, context);
            await insert.ExecuteNonQueryAsync(context.RequestAborted);
        }

        foreach (var line in request.Lines)
        {
            var product = productSnapshots[line.ProductId];
            var gross = FinancePurchasePolicy.Round(line.Quantity * line.UnitPrice);
            var lineTotal = FinancePurchasePolicy.CalculateInvoiceTotal(gross, line.Discount, 0m, line.Tax);
            await using var insertLine = new NpgsqlCommand($"INSERT INTO {Table("purchase_invoice_lines")} (id,invoice_id,product_id,sku_snapshot,name_snapshot,quantity,received_quantity,unit_price,discount,tax,tax_recoverable,line_total) VALUES (@id,@invoice,@product,@sku,@name,@quantity,0,@price,@discount,@tax,@tax_recoverable,@total)", connection, transaction);
            insertLine.Parameters.AddWithValue("id", Guid.NewGuid()); insertLine.Parameters.AddWithValue("invoice", id); insertLine.Parameters.AddWithValue("product", line.ProductId);
            insertLine.Parameters.AddWithValue("sku", product.Sku); insertLine.Parameters.AddWithValue("name", product.Name); insertLine.Parameters.AddWithValue("quantity", line.Quantity);
            insertLine.Parameters.AddWithValue("price", line.UnitPrice); insertLine.Parameters.AddWithValue("discount", line.Discount); insertLine.Parameters.AddWithValue("tax", line.Tax);
            insertLine.Parameters.AddWithValue("tax_recoverable", line.TaxRecoverable);
            insertLine.Parameters.AddWithValue("total", lineTotal); await insertLine.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await WriteEventAsync(connection, transaction, context, purchaseId is null ? "purchase.draft_created" : "purchase.draft_updated",
            new { id, request.SupplierId, request.DocumentType, request.DocumentNumber, request.Currency, request.Total }, DateTime.UtcNow);
        await transaction.CommitAsync(context.RequestAborted);
        return purchaseId is null ? Results.Created($"purchases/{id:D}", new { id, status = "Draft", total = request.Total })
            : Results.Ok(new { id, status = "Draft", total = request.Total });
    }

    private static async Task<IResult> PostPurchaseAsync(HttpContext context)
    {
        var id = RouteGuid(context, "id");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        var header = await ReadPurchaseHeaderAsync(connection, transaction, id, context.RequestAborted, forUpdate: true);
        if (header is null) return Results.NotFound(new { error = "purchase_not_found" });
        if (header.Status is "Posted" or "Paid" or "Partial")
        {
            await transaction.CommitAsync(context.RequestAborted);
            return Results.Ok(new { id, status = header.Status, replayed = true });
        }
        if (header.Status != "Draft") return Results.Conflict(new { error = "purchase_state_not_postable" });
        if (header.PaymentTerm == "Immediate") return Results.Conflict(new { error = "immediate_purchase_requires_payment" });
        var now = DateTime.UtcNow;
        var invoiceUsd = FinancePurchasePolicy.ToUsdEquivalent(header.Total, header.Currency, header.ExchangeRate);
        await using var charge = new NpgsqlCommand($"INSERT INTO {Table("supplier_payables")} (id,supplier_id,invoice_id,payment_id,entry_type,currency,amount,amount_usd_equivalent,applied_exchange_rate,fx_difference,reference,created_by,created_at_utc) VALUES (@id,@supplier,@invoice,NULL,'Invoice',@currency,@amount,@usd,@rate,0,@reference,@actor,@now)", connection, transaction);
        charge.Parameters.AddWithValue("id", Guid.NewGuid()); charge.Parameters.AddWithValue("supplier", header.SupplierId); charge.Parameters.AddWithValue("invoice", id);
        charge.Parameters.AddWithValue("currency", header.Currency); charge.Parameters.AddWithValue("amount", header.Total); charge.Parameters.AddWithValue("usd", invoiceUsd);
        charge.Parameters.AddWithValue("rate", header.ExchangeRate); charge.Parameters.AddWithValue("reference", $"{header.DocumentType} {header.DocumentNumber}");
        charge.Parameters.AddWithValue("actor", GetActorId(context)); charge.Parameters.AddWithValue("now", now); await charge.ExecuteNonQueryAsync(context.RequestAborted);
        await using var update = new NpgsqlCommand($"UPDATE {Table("purchase_invoices")} SET status='Posted',posted_at_utc=@now,updated_at_utc=@now WHERE id=@id AND status='Draft'", connection, transaction);
        update.Parameters.AddWithValue("now", now); update.Parameters.AddWithValue("id", id);
        if (await update.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "purchase_state_changed" });
        await WriteEventAsync(connection, transaction, context, "purchase.posted", new { id, header.Total, header.Currency, invoiceUsd }, now);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { id, status = "Posted", replayed = false });
    }

    private static async Task<IResult> PostImmediatePurchaseAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        var request = await context.Request.ReadFromJsonAsync<ImmediatePurchasePostingRequest>(cancellationToken: context.RequestAborted);
        if (!ValidatePayment(request?.Payment, out var validationMessage))
            return ValidationError("payment", validationMessage ?? "Indica un pago completo válido para la factura.");
        var paymentRequest = request!.Payment!;
        var requestHash = FinanceIdempotencyPolicy.HashPayment(invoiceId, paymentRequest);
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        var header = await ReadPurchaseHeaderAsync(connection, transaction, invoiceId, context.RequestAborted, forUpdate: true);
        if (header is null) return Results.NotFound(new { error = "purchase_not_found" });

        await using (var replay = new NpgsqlCommand($"SELECT id,idempotency_hash FROM {Table("supplier_payments")} WHERE idempotency_key=@key", connection, transaction))
        {
            replay.Parameters.AddWithValue("key", paymentRequest.IdempotencyKey.Trim());
            await using var reader = await replay.ExecuteReaderAsync(context.RequestAborted);
            if (await reader.ReadAsync(context.RequestAborted))
            {
                var replayedPaymentId = reader.GetGuid(0);
                var previousHash = reader.GetString(1);
                await reader.CloseAsync();
                await transaction.CommitAsync(context.RequestAborted);
                return string.Equals(previousHash, requestHash, StringComparison.Ordinal)
                    ? Results.Ok(new { id = invoiceId, paymentId = replayedPaymentId, status = header.Status, replayed = true })
                    : Results.Conflict(new { error = "supplier_payment_idempotency_conflict" });
            }
        }

        if (header.Status != "Draft") return Results.Conflict(new { error = "purchase_state_not_postable" });
        if (header.PaymentTerm != "Immediate") return Results.Conflict(new { error = "immediate_purchase_required" });

        var currentRate = await ReadCurrentUsdToVesRateAsync(connection, transaction, context.RequestAborted) ?? 0m;
        ImmediateSettlement settlement;
        try
        {
            settlement = FinancePurchasePolicy.CalculateImmediateSettlement(header.Total, header.Currency,
                paymentRequest.Amount, paymentRequest.Currency, currentRate, header.ExchangeRate);
        }
        catch (InvalidOperationException exception) when (exception.Message is "exchange_rate_unavailable" or "immediate_payment_must_equal_invoice_total")
        { return Results.Conflict(new { error = exception.Message, invoiceTotal = header.Total, currency = header.Currency }); }

        var now = DateTime.UtcNow;
        var paymentId = Guid.NewGuid();
        var invoiceUsd = FinancePurchasePolicy.ToUsdEquivalent(header.Total, header.Currency, header.ExchangeRate);
        await using (var charge = new NpgsqlCommand($"INSERT INTO {Table("supplier_payables")} (id,supplier_id,invoice_id,payment_id,entry_type,currency,amount,amount_usd_equivalent,applied_exchange_rate,fx_difference,reference,created_by,created_at_utc) VALUES (@id,@supplier,@invoice,NULL,'Invoice',@currency,@amount,@usd,@rate,0,@reference,@actor,@now)", connection, transaction))
        {
            charge.Parameters.AddWithValue("id", Guid.NewGuid()); charge.Parameters.AddWithValue("supplier", header.SupplierId); charge.Parameters.AddWithValue("invoice", invoiceId);
            charge.Parameters.AddWithValue("currency", header.Currency); charge.Parameters.AddWithValue("amount", header.Total); charge.Parameters.AddWithValue("usd", invoiceUsd);
            charge.Parameters.AddWithValue("rate", header.ExchangeRate); charge.Parameters.AddWithValue("reference", $"{header.DocumentType} {header.DocumentNumber}");
            charge.Parameters.AddWithValue("actor", GetActorId(context)); charge.Parameters.AddWithValue("now", now); await charge.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await using (var payment = new NpgsqlCommand($"INSERT INTO {Table("supplier_payments")} (id,invoice_id,payment_currency,amount,invoice_currency_amount,applied_exchange_rate,fx_difference,method,receiving_account,reference,paid_at_utc,created_by,idempotency_key,idempotency_hash,reversed_at_utc) VALUES (@id,@invoice,@currency,@amount,@principal,@rate,@fx,@method,@account,@reference,@now,@actor,@key,@hash,NULL)", connection, transaction))
        {
            payment.Parameters.AddWithValue("id", paymentId); payment.Parameters.AddWithValue("invoice", invoiceId); payment.Parameters.AddWithValue("currency", paymentRequest.Currency);
            payment.Parameters.AddWithValue("amount", paymentRequest.Amount); payment.Parameters.AddWithValue("principal", settlement.InvoiceCurrencyAmount);
            payment.Parameters.AddWithValue("rate", settlement.AppliedExchangeRate); payment.Parameters.AddWithValue("fx", (object?)settlement.FxDifferenceUsd ?? DBNull.Value);
            payment.Parameters.AddWithValue("method", paymentRequest.Method.Trim()); payment.Parameters.AddWithValue("account", paymentRequest.ReceivingAccount.Trim());
            payment.Parameters.AddWithValue("reference", (object?)TrimOrNull(paymentRequest.Reference) ?? DBNull.Value); payment.Parameters.AddWithValue("now", now);
            payment.Parameters.AddWithValue("actor", GetActorId(context)); payment.Parameters.AddWithValue("key", paymentRequest.IdempotencyKey.Trim()); payment.Parameters.AddWithValue("hash", requestHash);
            await payment.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await using (var ledger = new NpgsqlCommand($"INSERT INTO {Table("supplier_payables")} (id,supplier_id,invoice_id,payment_id,entry_type,currency,amount,amount_usd_equivalent,applied_exchange_rate,fx_difference,reference,created_by,created_at_utc) VALUES (@id,@supplier,@invoice,@payment,'Payment',@currency,@amount,@usd,@rate,@fx,@reference,@actor,@now)", connection, transaction))
        {
            ledger.Parameters.AddWithValue("id", Guid.NewGuid()); ledger.Parameters.AddWithValue("supplier", header.SupplierId); ledger.Parameters.AddWithValue("invoice", invoiceId);
            ledger.Parameters.AddWithValue("payment", paymentId); ledger.Parameters.AddWithValue("currency", header.Currency); ledger.Parameters.AddWithValue("amount", -settlement.InvoiceCurrencyAmount);
            ledger.Parameters.AddWithValue("usd", (object?)(settlement.PaymentUsdEquivalent is null ? null : -settlement.PaymentUsdEquivalent.Value) ?? DBNull.Value);
            ledger.Parameters.AddWithValue("rate", settlement.AppliedExchangeRate); ledger.Parameters.AddWithValue("fx", (object?)settlement.FxDifferenceUsd ?? DBNull.Value);
            ledger.Parameters.AddWithValue("reference", (object?)TrimOrNull(paymentRequest.Reference) ?? DBNull.Value); ledger.Parameters.AddWithValue("actor", GetActorId(context)); ledger.Parameters.AddWithValue("now", now);
            await ledger.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await using (var update = new NpgsqlCommand($"UPDATE {Table("purchase_invoices")} SET status='Paid',paid_amount=@amount,posted_at_utc=@now,updated_at_utc=@now WHERE id=@id AND status='Draft'", connection, transaction))
        {
            update.Parameters.AddWithValue("amount", header.Total); update.Parameters.AddWithValue("now", now); update.Parameters.AddWithValue("id", invoiceId);
            if (await update.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "purchase_state_changed" });
        }
        await WriteEventAsync(connection, transaction, context, "purchase.posted",
            new { invoiceId, header.Total, header.Currency, invoiceUsd, paymentTerm = header.PaymentTerm, paymentId }, now);
        await WriteEventAsync(connection, transaction, context, "supplier.payment_recorded",
            new { paymentId, invoiceId, paymentRequest.Amount, paymentRequest.Currency, invoiceCurrencyAmount = settlement.InvoiceCurrencyAmount,
                fxDifference = settlement.FxDifferenceUsd, newBalance = 0m, paymentTerm = "Immediate" }, now);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Created($"payments/{paymentId:D}", new
        {
            id = invoiceId, paymentId, status = "Paid", invoiceCurrencyAmount = settlement.InvoiceCurrencyAmount,
            currency = header.Currency, balance = 0m, replayed = false
        });
    }

    private static async Task<IResult> ListReceiptsAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"""
            SELECT r.id,r.invoice_id,r.warehouse_id,r.idempotency_key,r.created_at_utc,r.completed_at_utc,r.reversed_at_utc,
                   l.invoice_line_id,l.product_id,l.quantity,l.unit_cost,l.cost_currency,l.cost_exchange_rate
            FROM {Table("purchase_receipts")} r LEFT JOIN {Table("purchase_receipt_lines")} l ON l.receipt_id=r.id
            WHERE r.invoice_id=@invoice ORDER BY r.created_at_utc,l.invoice_line_id
            """, connection);
        command.Parameters.AddWithValue("invoice", invoiceId);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var receipts = new Dictionary<Guid, List<PurchaseReceiptLineInfo>>();
        var headers = new Dictionary<Guid, PurchaseReceiptHeaderInfo>();
        while (await reader.ReadAsync(context.RequestAborted))
        {
            var id = reader.GetGuid(0);
            if (!headers.ContainsKey(id))
            {
                headers[id] = new(id, reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                    reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6));
                receipts[id] = [];
            }
            if (!reader.IsDBNull(7)) receipts[id].Add(new(reader.GetGuid(7), reader.GetGuid(8), reader.GetDecimal(9),
                reader.GetDecimal(10), reader.GetString(11), reader.GetDecimal(12)));
        }
        return Results.Ok(headers.Values.Select(header => new PurchaseReceiptInfo(header, receipts[header.Id])).ToArray());
    }

    private static async Task<IResult> ReceivePurchaseAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        var request = await context.Request.ReadFromJsonAsync<PurchaseReceiptRequest>(cancellationToken: context.RequestAborted);
        if (request is null || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length is < 8 or > 128 || request.IdempotencyKey.Any(char.IsControl) ||
            request.Lines is null || request.Lines.Count is < 1 or > 100 ||
            request.Lines.Any(line => line.InvoiceLineId == Guid.Empty || line.Quantity <= 0m || decimal.Round(line.Quantity, 3) != line.Quantity) ||
            request.Lines.Select(line => line.InvoiceLineId).Distinct().Count() != request.Lines.Count)
            return ValidationError("receipt", "Indica una clave de operación y cantidades válidas para líneas de factura distintas.");
        var requestHash = HashReceipt(invoiceId, request);
        Guid receiptId;
        var receiptLines = new List<ReceiptEffectLine>();
        await using (var connection = await OpenAsync(context, context.RequestAborted))
        await using (var transaction = await connection.BeginTransactionAsync(context.RequestAborted))
        {
            await using (var replay = new NpgsqlCommand($"SELECT id,idempotency_hash,completed_at_utc,reversed_at_utc FROM {Table("purchase_receipts")} WHERE idempotency_key=@key", connection, transaction))
            {
                replay.Parameters.AddWithValue("key", request.IdempotencyKey.Trim());
                var previous = await FinanceReceiptReplayReader.ReadAndDisposeAsync(
                    await replay.ExecuteReaderAsync(context.RequestAborted), context.RequestAborted);
                if (previous is not null)
                {
                    receiptId = previous.Id;
                    if (!string.Equals(previous.IdempotencyHash, requestHash, StringComparison.Ordinal)) return Results.Conflict(new { error = "purchase_receipt_idempotency_conflict" });
                    if (previous.ReversedAtUtc.HasValue) return Results.Conflict(new { error = "purchase_receipt_already_reversed" });
                    if (previous.CompletedAtUtc.HasValue) { await transaction.CommitAsync(context.RequestAborted); return Results.Ok(new { id = receiptId, status = "Received", replayed = true }); }
                    receiptLines = await LoadReceiptEffectLinesAsync(connection, transaction, receiptId, context.RequestAborted);
                    if (receiptLines.Count == 0) return Results.Conflict(new { error = "purchase_receipt_pending_without_lines" });
                    await transaction.CommitAsync(context.RequestAborted);
                }
                else
                {
                    receiptId = Guid.NewGuid();
                    var header = await ReadPurchaseHeaderAsync(connection, transaction, invoiceId, context.RequestAborted, forUpdate: true);
                    if (header is null) return Results.NotFound(new { error = "purchase_not_found" });
                    if (header.Status is not ("Posted" or "Partial" or "Paid")) return Results.Conflict(new { error = "posted_purchase_required" });
                    var invoiceLines = await LoadInvoiceReceiptCostsAsync(connection, transaction, invoiceId, context.RequestAborted);
                    var byId = invoiceLines.ToDictionary(line => line.InvoiceLineId);
                    if (request.Lines.Any(line => !byId.ContainsKey(line.InvoiceLineId))) return Results.Conflict(new { error = "purchase_invoice_line_not_found" });
                    foreach (var requestedLine in request.Lines)
                    {
                        var line = byId[requestedLine.InvoiceLineId];
                        if (requestedLine.Quantity > line.Quantity - line.ReceivedQuantity) return Results.Conflict(new { error = "purchase_receipt_exceeds_unreceived_quantity", invoiceLineId = line.InvoiceLineId });
                    }
                    var costs = new Dictionary<Guid, (decimal UnitCost, string Currency, decimal Rate)>();
                    foreach (var line in invoiceLines)
                    {
                        var cost = FinancePurchasePolicy.CalculateItemUnitCost(line.Quantity, line.UnitPrice, line.Discount,
                            header.Currency, line.ProductCurrency, header.ExchangeRate);
                        var conversion = header.Currency == line.ProductCurrency ? 1m : header.Currency == "USD" ? header.ExchangeRate : 1m / header.ExchangeRate;
                        costs[line.InvoiceLineId] = (cost, line.ProductCurrency, conversion);
                    }
                    var now = DateTime.UtcNow;
                    await using (var insert = new NpgsqlCommand($"INSERT INTO {Table("purchase_receipts")} (id,invoice_id,idempotency_key,idempotency_hash,warehouse_id,received_by,terminal_id,created_at_utc,completed_at_utc) VALUES (@id,@invoice,@key,@hash,@warehouse,@actor,@terminal,@now,NULL)", connection, transaction))
                    {
                        insert.Parameters.AddWithValue("id", receiptId); insert.Parameters.AddWithValue("invoice", invoiceId); insert.Parameters.AddWithValue("key", request.IdempotencyKey.Trim());
                        insert.Parameters.AddWithValue("hash", requestHash); insert.Parameters.AddWithValue("warehouse", (object?)request.WarehouseId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("actor", GetActorId(context)); insert.Parameters.AddWithValue("terminal", GetTerminalId(context)); insert.Parameters.AddWithValue("now", now);
                        await insert.ExecuteNonQueryAsync(context.RequestAborted);
                    }
                    foreach (var requestedLine in request.Lines)
                    {
                        var line = byId[requestedLine.InvoiceLineId]; var cost = costs[line.InvoiceLineId];
                        await using var insertLine = new NpgsqlCommand($"INSERT INTO {Table("purchase_receipt_lines")} (id,receipt_id,invoice_line_id,product_id,quantity,unit_cost,cost_currency,cost_exchange_rate) VALUES (@id,@receipt,@line,@product,@quantity,@cost,@currency,@rate)", connection, transaction);
                        insertLine.Parameters.AddWithValue("id", Guid.NewGuid()); insertLine.Parameters.AddWithValue("receipt", receiptId); insertLine.Parameters.AddWithValue("line", line.InvoiceLineId);
                        insertLine.Parameters.AddWithValue("product", line.ProductId); insertLine.Parameters.AddWithValue("quantity", requestedLine.Quantity); insertLine.Parameters.AddWithValue("cost", cost.UnitCost);
                        insertLine.Parameters.AddWithValue("currency", cost.Currency); insertLine.Parameters.AddWithValue("rate", cost.Rate); await insertLine.ExecuteNonQueryAsync(context.RequestAborted);
                        receiptLines.Add(new(line.InvoiceLineId, line.ProductId, requestedLine.Quantity, cost.UnitCost, cost.Currency));
                    }
                    await WriteEventAsync(connection, transaction, context, "purchase.receipt_started", new { receiptId, invoiceId, request.WarehouseId, lineCount = request.Lines.Count }, now);
                    await transaction.CommitAsync(context.RequestAborted);
                }
            }
        }

        bool inventoryReplay;
        try
        {
            var inventory = context.RequestServices.GetRequiredService<IPosInventoryReceiptService>();
            inventoryReplay = await inventory.ApplyReceiptAsync(receiptId, request.WarehouseId,
                receiptLines.Select(line => new PosInventoryReceiptLine(line.ProductId, line.Quantity, line.UnitCost, line.CostCurrency)).ToArray(),
                GetActorId(context), GetTerminalId(context), context.RequestAborted);
        }
        catch (InvalidOperationException exception) when (exception.Message is "purchase_receipt_idempotency_conflict" or "purchase_receipt_already_reversed" or "purchase_warehouse_not_available" or "purchase_product_unavailable" or "purchase_cost_currency_mismatch" or "purchase_product_stock_missing")
        { return Results.Conflict(new { error = exception.Message }); }

        await using (var connection = await OpenAsync(context, context.RequestAborted))
        await using (var transaction = await connection.BeginTransactionAsync(context.RequestAborted))
        {
            await using var complete = new NpgsqlCommand($"UPDATE {Table("purchase_receipts")} SET completed_at_utc=@now WHERE id=@id AND completed_at_utc IS NULL", connection, transaction);
            complete.Parameters.AddWithValue("now", DateTime.UtcNow); complete.Parameters.AddWithValue("id", receiptId);
            if (await complete.ExecuteNonQueryAsync(context.RequestAborted) == 1)
            {
                foreach (var line in receiptLines)
                {
                    await using var markReceived = new NpgsqlCommand($"UPDATE {Table("purchase_invoice_lines")} SET received_quantity=received_quantity+@quantity WHERE id=@line AND invoice_id=@invoice", connection, transaction);
                    markReceived.Parameters.AddWithValue("quantity", line.Quantity); markReceived.Parameters.AddWithValue("line", line.InvoiceLineId); markReceived.Parameters.AddWithValue("invoice", invoiceId);
                    if (await markReceived.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "purchase_invoice_line_changed" });
                }
                await WriteEventAsync(connection, transaction, context, "purchase.received", new { receiptId, invoiceId, request.WarehouseId }, DateTime.UtcNow);
            }
            await transaction.CommitAsync(context.RequestAborted);
        }
        return Results.Ok(new { id = receiptId, status = "Received", replayed = inventoryReplay });
    }

    private static async Task<IResult> ReverseReceiptAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        var receiptId = RouteGuid(context, "receiptId");
        var request = await context.Request.ReadFromJsonAsync<ReverseRequest>(cancellationToken: context.RequestAborted);
        var reason = (request?.Reason ?? string.Empty).Trim();
        if (reason.Length is < 3 or > 250) return ValidationError("reason", "Explica el motivo del reverso (3 a 250 caracteres).");

        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        bool completed;
        bool alreadyReversed;
        await using (var read = new NpgsqlCommand($"SELECT completed_at_utc IS NOT NULL,reversed_at_utc IS NOT NULL FROM {Table("purchase_receipts")} WHERE id=@receipt AND invoice_id=@invoice FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("receipt", receiptId); read.Parameters.AddWithValue("invoice", invoiceId);
            await using var reader = await read.ExecuteReaderAsync(context.RequestAborted);
            if (!await reader.ReadAsync(context.RequestAborted)) return Results.NotFound(new { error = "purchase_receipt_not_found" });
            completed = reader.GetBoolean(0); alreadyReversed = reader.GetBoolean(1);
        }
        if (!completed) return Results.Conflict(new { error = "purchase_receipt_not_completed" });
        if (alreadyReversed)
        {
            await transaction.CommitAsync(context.RequestAborted);
            return Results.Ok(new { id = receiptId, status = "Reversed", replayed = true });
        }

        try
        {
            var inventory = context.RequestServices.GetRequiredService<IPosInventoryReceiptService>();
            await inventory.ReverseReceiptAsync(receiptId, GetActorId(context), GetTerminalId(context), context.RequestAborted);
        }
        catch (InvalidOperationException exception) when (exception.Message is "purchase_receipt_not_reversible" or "purchase_receipt_effect_not_found" or "purchase_receipt_effect_lines_missing" or "purchase_receipt_product_missing")
        {
            return Results.Conflict(new { error = exception.Message });
        }

        var receiptLines = await LoadReceiptEffectLinesAsync(connection, transaction, receiptId, context.RequestAborted);
        foreach (var line in receiptLines)
        {
            await using var adjust = new NpgsqlCommand($"UPDATE {Table("purchase_invoice_lines")} SET received_quantity=received_quantity-@quantity WHERE id=@line AND invoice_id=@invoice AND received_quantity>=@quantity", connection, transaction);
            adjust.Parameters.AddWithValue("quantity", line.Quantity); adjust.Parameters.AddWithValue("line", line.InvoiceLineId); adjust.Parameters.AddWithValue("invoice", invoiceId);
            if (await adjust.ExecuteNonQueryAsync(context.RequestAborted) != 1)
                return Results.Conflict(new { error = "purchase_receipt_quantity_state_changed" });
        }
        var now = DateTime.UtcNow;
        await using (var mark = new NpgsqlCommand($"UPDATE {Table("purchase_receipts")} SET reversed_at_utc=@now WHERE id=@id AND reversed_at_utc IS NULL", connection, transaction))
        { mark.Parameters.AddWithValue("now", now); mark.Parameters.AddWithValue("id", receiptId); if (await mark.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "purchase_receipt_state_changed" }); }
        await WriteEventAsync(connection, transaction, context, "purchase.receipt_reversed", new { receiptId, invoiceId, reason }, now);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { id = receiptId, status = "Reversed", replayed = false });
    }

    private static async Task<List<ReceiptEffectLine>> LoadReceiptEffectLinesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid receiptId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT invoice_line_id,product_id,quantity,unit_cost,cost_currency FROM {Table("purchase_receipt_lines")} WHERE receipt_id=@id ORDER BY invoice_line_id", connection, transaction);
        command.Parameters.AddWithValue("id", receiptId); await using var reader = await command.ExecuteReaderAsync(ct);
        var lines = new List<ReceiptEffectLine>(); while (await reader.ReadAsync(ct)) lines.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetString(4)));
        return lines;
    }

    private static async Task<List<InvoiceReceiptCostLine>> LoadInvoiceReceiptCostsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid invoiceId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(FinanceSql.LoadInvoiceReceiptCosts(Table("purchase_invoice_lines")), connection, transaction);
        command.Parameters.AddWithValue("invoice", invoiceId); await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<InvoiceReceiptCostLine>(); while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetBoolean(7), reader.GetString(8)));
        return rows;
    }

    private static async Task<IResult> ReversePurchaseAsync(HttpContext context)
    {
        var id = RouteGuid(context, "id");
        var request = await context.Request.ReadFromJsonAsync<ReverseRequest>(cancellationToken: context.RequestAborted);
        var reason = (request?.Reason ?? string.Empty).Trim();
        if (reason.Length is < 3 or > 250) return ValidationError("reason", "Explica el motivo del reverso (3 a 250 caracteres).");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        var header = await ReadPurchaseHeaderAsync(connection, transaction, id, context.RequestAborted, forUpdate: true);
        if (header is null) return Results.NotFound(new { error = "purchase_not_found" });
        if (header.Status == "Reversed") { await transaction.CommitAsync(context.RequestAborted); return Results.Ok(new { id, status = "Reversed", replayed = true }); }
        if (header.Status == "Draft")
        {
            await using var cancel = new NpgsqlCommand($"UPDATE {Table("purchase_invoices")} SET status='Reversed',reversed_at_utc=@now,updated_at_utc=@now WHERE id=@id AND status='Draft'", connection, transaction);
            cancel.Parameters.AddWithValue("now", DateTime.UtcNow); cancel.Parameters.AddWithValue("id", id); await cancel.ExecuteNonQueryAsync(context.RequestAborted);
        }
        else if (header.Status is "Posted" or "Partial" or "Paid")
        {
            await using var received = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {Table("purchase_receipts")} WHERE invoice_id=@id AND completed_at_utc IS NOT NULL AND reversed_at_utc IS NULL)", connection, transaction);
            received.Parameters.AddWithValue("id", id);
            if ((bool)(await received.ExecuteScalarAsync(context.RequestAborted) ?? false))
                return Results.Conflict(new { error = "purchase_receipts_must_be_reversed_first" });
            var balance = await ReadPayableBalanceAsync(connection, transaction, id, context.RequestAborted);
            if (balance != header.Total) return Results.Conflict(new { error = "reverse_supplier_payments_first", balance, expected = header.Total });
            var now = DateTime.UtcNow;
            await using var reversal = new NpgsqlCommand($"INSERT INTO {Table("supplier_payables")} (id,supplier_id,invoice_id,payment_id,entry_type,currency,amount,amount_usd_equivalent,applied_exchange_rate,fx_difference,reference,created_by,created_at_utc) VALUES (@id,@supplier,@invoice,NULL,'InvoiceReversal',@currency,@amount,@usd,@rate,0,@reference,@actor,@now)", connection, transaction);
            reversal.Parameters.AddWithValue("id", Guid.NewGuid()); reversal.Parameters.AddWithValue("supplier", header.SupplierId); reversal.Parameters.AddWithValue("invoice", id);
            reversal.Parameters.AddWithValue("currency", header.Currency); reversal.Parameters.AddWithValue("amount", -header.Total);
            reversal.Parameters.AddWithValue("usd", -FinancePurchasePolicy.ToUsdEquivalent(header.Total, header.Currency, header.ExchangeRate));
            reversal.Parameters.AddWithValue("rate", header.ExchangeRate); reversal.Parameters.AddWithValue("reference", reason);
            reversal.Parameters.AddWithValue("actor", GetActorId(context)); reversal.Parameters.AddWithValue("now", now); await reversal.ExecuteNonQueryAsync(context.RequestAborted);
            await using var mark = new NpgsqlCommand($"UPDATE {Table("purchase_invoices")} SET status='Reversed',reversed_at_utc=@now,updated_at_utc=@now WHERE id=@id AND status IN ('Posted','Partial','Paid')", connection, transaction);
            mark.Parameters.AddWithValue("now", now); mark.Parameters.AddWithValue("id", id); await mark.ExecuteNonQueryAsync(context.RequestAborted);
        }
        else return Results.Conflict(new { error = "purchase_not_reversible" });
        await WriteEventAsync(connection, transaction, context, "purchase.reversed", new { id, reason }, DateTime.UtcNow);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { id, status = "Reversed", replayed = false });
    }

    private static async Task<IResult> ListPayablesAsync(HttpContext context)
    {
        var page = ReadPositiveInt(context.Request.Query["page"], 1, 1_000_000);
        var pageSize = ReadPositiveInt(context.Request.Query["pageSize"], 1000, 1000);
        var offset = ((long)page - 1) * pageSize;
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"""
            SELECT i.id,s.id,s.legal_name,i.document_type,i.document_number,i.currency,i.issue_date,i.due_date,
                   i.total,COALESCE(sum(p.amount),0)::numeric(18,4),
                   COALESCE(sum(p.amount_usd_equivalent),0)::numeric(18,4),i.exchange_rate
            FROM {Table("purchase_invoices")} i JOIN {Table("suppliers")} s ON s.id=i.supplier_id
            JOIN {Table("supplier_payables")} p ON p.invoice_id=i.id
            WHERE i.status IN ('Posted','Partial','Paid') GROUP BY i.id,s.id,s.legal_name
            HAVING sum(p.amount)>0 ORDER BY i.due_date NULLS LAST,i.issue_date,i.document_number,i.id
            LIMIT @pageSize OFFSET @offset
            """, connection);
        command.Parameters.AddWithValue("pageSize", pageSize);
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<PayableInfo>();
        while (await reader.ReadAsync(context.RequestAborted))
            rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)), reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)),
                reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11)));
        return Results.Ok(rows);
    }

    private static int ReadPositiveInt(string? value, int fallback, int maximum)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, maximum)
            : fallback;

    private static async Task<IResult> ListPaymentsAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"SELECT id,invoice_id,payment_currency,amount,invoice_currency_amount,applied_exchange_rate,fx_difference,method,receiving_account,reference,paid_at_utc,reversed_at_utc FROM {Table("supplier_payments")} WHERE invoice_id=@id ORDER BY paid_at_utc,id", connection);
        command.Parameters.AddWithValue("id", invoiceId);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var rows = new List<SupplierPaymentInfo>();
        while (await reader.ReadAsync(context.RequestAborted)) rows.Add(ReadPayment(reader));
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreatePaymentAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        var request = await context.Request.ReadFromJsonAsync<SupplierPaymentRequest>(cancellationToken: context.RequestAborted);
        if (!ValidatePayment(request, out var validationMessage)) return ValidationError("payment", validationMessage ?? "El abono no es válido.");
        var requestHash = FinanceIdempotencyPolicy.HashPayment(invoiceId, request!);
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        await using (var replay = new NpgsqlCommand($"SELECT id,idempotency_hash FROM {Table("supplier_payments")} WHERE idempotency_key=@key", connection, transaction))
        {
            replay.Parameters.AddWithValue("key", request!.IdempotencyKey.Trim());
            await using var reader = await replay.ExecuteReaderAsync(context.RequestAborted);
            if (await reader.ReadAsync(context.RequestAborted))
            {
                var replayedPaymentId = reader.GetGuid(0); var hash = reader.GetString(1); await reader.CloseAsync();
                await transaction.CommitAsync(context.RequestAborted);
                return string.Equals(hash, requestHash, StringComparison.Ordinal)
                    ? Results.Ok(new { id = replayedPaymentId, replayed = true })
                    : Results.Conflict(new { error = "supplier_payment_idempotency_conflict" });
            }
        }

        var header = await ReadPurchaseHeaderAsync(connection, transaction, invoiceId, context.RequestAborted, forUpdate: true);
        if (header is null) return Results.NotFound(new { error = "purchase_not_found" });
        if (header.Status is not ("Posted" or "Partial" or "Paid")) return Results.Conflict(new { error = "posted_purchase_required" });
        var currentRate = await ReadCurrentUsdToVesRateAsync(connection, transaction, context.RequestAborted);
        var conversionRate = request!.Currency == header.Currency ? 1m : currentRate ?? 0m;
        if (request.Currency != header.Currency && conversionRate <= 0m) return Results.Conflict(new { error = "exchange_rate_unavailable" });
        var principalReduction = FinancePurchasePolicy.ConvertPaymentToInvoiceCurrency(request.Amount, request.Currency, header.Currency, conversionRate == 0m ? 1m : conversionRate);
        var outstanding = await ReadPayableBalanceAsync(connection, transaction, invoiceId, context.RequestAborted);
        if (principalReduction > outstanding) return Results.Conflict(new { error = "payment_exceeds_supplier_balance", outstanding, currency = header.Currency });

        var paymentId = Guid.NewGuid(); var now = DateTime.UtcNow;
        var usdRate = currentRate ?? (header.Currency == "USD" && request.Currency == "USD" ? 1m : 0m);
        decimal? paymentUsd = usdRate > 0m ? FinancePurchasePolicy.ToUsdEquivalent(request.Amount, request.Currency, usdRate) : null;
        decimal? fxDifference = null;
        if (paymentUsd.HasValue && (header.Currency == "USD" || header.ExchangeRate > 0m))
            fxDifference = FinancePurchasePolicy.CalculateRealizedFxGainUsd(principalReduction, header.Currency, header.ExchangeRate,
                request.Amount, request.Currency, usdRate);
        var appliedRate = request.Currency == header.Currency ? 1m : request.Currency == "USD" ? currentRate!.Value : 1m / currentRate!.Value;
        await using (var insert = new NpgsqlCommand($"INSERT INTO {Table("supplier_payments")} (id,invoice_id,payment_currency,amount,invoice_currency_amount,applied_exchange_rate,fx_difference,method,receiving_account,reference,paid_at_utc,created_by,idempotency_key,idempotency_hash,reversed_at_utc) VALUES (@id,@invoice,@currency,@amount,@principal,@rate,@fx,@method,@account,@reference,@now,@actor,@key,@hash,NULL)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", paymentId); insert.Parameters.AddWithValue("invoice", invoiceId); insert.Parameters.AddWithValue("currency", request.Currency);
            insert.Parameters.AddWithValue("amount", request.Amount); insert.Parameters.AddWithValue("principal", principalReduction); insert.Parameters.AddWithValue("rate", appliedRate);
            insert.Parameters.AddWithValue("fx", (object?)fxDifference ?? DBNull.Value); insert.Parameters.AddWithValue("method", request.Method.Trim()); insert.Parameters.AddWithValue("account", request.ReceivingAccount.Trim());
            insert.Parameters.AddWithValue("reference", (object?)TrimOrNull(request.Reference) ?? DBNull.Value); insert.Parameters.AddWithValue("now", now); insert.Parameters.AddWithValue("actor", GetActorId(context));
            insert.Parameters.AddWithValue("key", request.IdempotencyKey.Trim()); insert.Parameters.AddWithValue("hash", requestHash); await insert.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await using (var ledger = new NpgsqlCommand($"INSERT INTO {Table("supplier_payables")} (id,supplier_id,invoice_id,payment_id,entry_type,currency,amount,amount_usd_equivalent,applied_exchange_rate,fx_difference,reference,created_by,created_at_utc) VALUES (@id,@supplier,@invoice,@payment,'Payment',@currency,@amount,@usd,@rate,@fx,@reference,@actor,@now)", connection, transaction))
        {
            ledger.Parameters.AddWithValue("id", Guid.NewGuid()); ledger.Parameters.AddWithValue("supplier", header.SupplierId); ledger.Parameters.AddWithValue("invoice", invoiceId); ledger.Parameters.AddWithValue("payment", paymentId);
            ledger.Parameters.AddWithValue("currency", header.Currency); ledger.Parameters.AddWithValue("amount", -principalReduction); ledger.Parameters.AddWithValue("usd", (object?)(paymentUsd is null ? null : -paymentUsd.Value) ?? DBNull.Value);
            ledger.Parameters.AddWithValue("rate", appliedRate); ledger.Parameters.AddWithValue("fx", (object?)fxDifference ?? DBNull.Value); ledger.Parameters.AddWithValue("reference", (object?)TrimOrNull(request.Reference) ?? DBNull.Value);
            ledger.Parameters.AddWithValue("actor", GetActorId(context)); ledger.Parameters.AddWithValue("now", now); await ledger.ExecuteNonQueryAsync(context.RequestAborted);
        }
        var newBalance = FinancePurchasePolicy.Round(outstanding - principalReduction);
        var newStatus = newBalance == 0m ? "Paid" : "Partial";
        await using (var update = new NpgsqlCommand($"UPDATE {Table("purchase_invoices")} SET status=@status,paid_amount=paid_amount+@amount,updated_at_utc=@now WHERE id=@invoice", connection, transaction))
        { update.Parameters.AddWithValue("status", newStatus); update.Parameters.AddWithValue("amount", principalReduction); update.Parameters.AddWithValue("now", now); update.Parameters.AddWithValue("invoice", invoiceId); await update.ExecuteNonQueryAsync(context.RequestAborted); }
        await WriteEventAsync(connection, transaction, context, "supplier.payment_recorded",
            new { paymentId, invoiceId, request.Amount, request.Currency, invoiceCurrencyAmount = principalReduction, fxDifference, newBalance }, now);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Created($"payments/{paymentId:D}", new { id = paymentId, invoiceCurrencyAmount = principalReduction, currency = header.Currency, balance = newBalance, replayed = false });
    }

    private static async Task<IResult> ReversePaymentAsync(HttpContext context)
    {
        var paymentId = RouteGuid(context, "id");
        var request = await context.Request.ReadFromJsonAsync<ReverseRequest>(cancellationToken: context.RequestAborted);
        var reason = (request?.Reason ?? string.Empty).Trim();
        if (reason.Length is < 3 or > 250) return ValidationError("reason", "Explica el motivo del reverso (3 a 250 caracteres).");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        var payment = await ReadPaymentForUpdateAsync(connection, transaction, paymentId, context.RequestAborted);
        if (payment is null) return Results.NotFound(new { error = "supplier_payment_not_found" });
        if (payment.ReversedAtUtc.HasValue) { await transaction.CommitAsync(context.RequestAborted); return Results.Ok(new { id = paymentId, replayed = true }); }
        var header = await ReadPurchaseHeaderAsync(connection, transaction, payment.InvoiceId, context.RequestAborted, forUpdate: true);
        if (header is null) return Results.NotFound(new { error = "purchase_not_found" });
        var now = DateTime.UtcNow;
        await using (var mark = new NpgsqlCommand($"UPDATE {Table("supplier_payments")} SET reversed_at_utc=@now WHERE id=@id AND reversed_at_utc IS NULL", connection, transaction))
        { mark.Parameters.AddWithValue("now", now); mark.Parameters.AddWithValue("id", paymentId); if (await mark.ExecuteNonQueryAsync(context.RequestAborted) != 1) return Results.Conflict(new { error = "payment_state_changed" }); }
        await using (var reversal = new NpgsqlCommand($"INSERT INTO {Table("supplier_payables")} (id,supplier_id,invoice_id,payment_id,entry_type,currency,amount,amount_usd_equivalent,applied_exchange_rate,fx_difference,reference,created_by,created_at_utc) VALUES (@id,@supplier,@invoice,@payment,'PaymentReversal',@currency,@amount,@usd,@rate,@fx,@reference,@actor,@now)", connection, transaction))
        {
            reversal.Parameters.AddWithValue("id", Guid.NewGuid()); reversal.Parameters.AddWithValue("supplier", header.SupplierId); reversal.Parameters.AddWithValue("invoice", payment.InvoiceId); reversal.Parameters.AddWithValue("payment", paymentId);
            reversal.Parameters.AddWithValue("currency", header.Currency); reversal.Parameters.AddWithValue("amount", payment.InvoiceCurrencyAmount);
            reversal.Parameters.AddWithValue("usd", (object?)(payment.UsdEquivalent is null ? null : -payment.UsdEquivalent.Value) ?? DBNull.Value);
            reversal.Parameters.AddWithValue("rate", payment.AppliedExchangeRate); reversal.Parameters.AddWithValue("fx", (object?)(payment.FxDifference is null ? null : -payment.FxDifference.Value) ?? DBNull.Value);
            reversal.Parameters.AddWithValue("reference", reason); reversal.Parameters.AddWithValue("actor", GetActorId(context)); reversal.Parameters.AddWithValue("now", now); await reversal.ExecuteNonQueryAsync(context.RequestAborted);
        }
        var balance = await ReadPayableBalanceAsync(connection, transaction, payment.InvoiceId, context.RequestAborted);
        if (header.Status == "Reversed") return Results.Conflict(new { error = "purchase_already_reversed" });
        var status = balance == 0m ? "Paid" : balance == header.Total ? "Posted" : "Partial";
        await using (var update = new NpgsqlCommand($"UPDATE {Table("purchase_invoices")} SET status=@status,paid_amount=GREATEST(0,paid_amount-@amount),updated_at_utc=@now WHERE id=@id", connection, transaction))
        { update.Parameters.AddWithValue("status", status); update.Parameters.AddWithValue("amount", payment.InvoiceCurrencyAmount); update.Parameters.AddWithValue("now", now); update.Parameters.AddWithValue("id", payment.InvoiceId); await update.ExecuteNonQueryAsync(context.RequestAborted); }
        await WriteEventAsync(connection, transaction, context, "supplier.payment_reversed", new { paymentId, payment.InvoiceId, reason }, now);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { id = paymentId, reversed = true, balance });
    }

    private static async Task<IResult> GetPriceSuggestionMarginsAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        var margins = await ReadPriceSuggestionMarginsAsync(connection, null, context.RequestAborted);
        var costBasis = await ReadPriceSuggestionCostBasisAsync(connection, null, context.RequestAborted);
        return Results.Ok(new PriceSuggestionMarginSettings(
            GetMarginPercent(margins, "Price1"),
            GetMarginPercent(margins, "Price2"),
            GetMarginPercent(margins, "Price3"), costBasis));
    }

    private static async Task<IResult> SetPriceSuggestionMarginsAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<PriceSuggestionMarginRequest>(cancellationToken: context.RequestAborted);
        if (request is null || !ValidMarginPercent(request.Price1MarginPercent) || !ValidMarginPercent(request.Price2MarginPercent) ||
            !ValidMarginPercent(request.Price3MarginPercent))
            return ValidationError("margins", "Cada margen debe ser un porcentaje entre 0 y menos de 100, con hasta dos decimales. Deja vacío un nivel para desactivar su sugerencia.");
        if (request.CostBasis is not null && !FinancePriceSuggestionCostPolicy.IsSupported(request.CostBasis))
            return ValidationError("costBasis", "Selecciona Promedio ponderado o Último costo recibido.");

        var configured = new (string Tier, decimal? Percent)[]
        {
            ("Price1", request.Price1MarginPercent), ("Price2", request.Price2MarginPercent), ("Price3", request.Price3MarginPercent)
        };
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
        await using (var settingLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(842017, 3)", connection, transaction))
            await settingLock.ExecuteScalarAsync(context.RequestAborted);
        var storedBasis = await ReadPriceSuggestionCostBasisAsync(connection, transaction, context.RequestAborted);
        var costBasis = FinancePriceSuggestionCostPolicy.ResolveBasis(request.CostBasis, storedBasis);
        await using (var saveBasis = new NpgsqlCommand($"INSERT INTO {Table("price_suggestion_settings")} (setting_key,cost_basis,updated_by,updated_at_utc) VALUES ('default',@basis,@actor,@now) ON CONFLICT (setting_key) DO UPDATE SET cost_basis=EXCLUDED.cost_basis,updated_by=EXCLUDED.updated_by,updated_at_utc=EXCLUDED.updated_at_utc", connection, transaction))
        {
            saveBasis.Parameters.AddWithValue("basis", costBasis);
            saveBasis.Parameters.AddWithValue("actor", GetActorId(context));
            saveBasis.Parameters.AddWithValue("now", now);
            await saveBasis.ExecuteNonQueryAsync(context.RequestAborted);
        }
        foreach (var (tier, percent) in configured)
        {
            if (percent is null)
            {
                await using var delete = new NpgsqlCommand($"DELETE FROM {Table("price_suggestion_margins")} WHERE price_tier=@tier", connection, transaction);
                delete.Parameters.AddWithValue("tier", tier); await delete.ExecuteNonQueryAsync(context.RequestAborted);
                continue;
            }
            await using var upsert = new NpgsqlCommand($"INSERT INTO {Table("price_suggestion_margins")} (price_tier,margin_rate,updated_by,updated_at_utc) VALUES (@tier,@margin,@actor,@now) ON CONFLICT (price_tier) DO UPDATE SET margin_rate=EXCLUDED.margin_rate,updated_by=EXCLUDED.updated_by,updated_at_utc=EXCLUDED.updated_at_utc", connection, transaction);
            upsert.Parameters.AddWithValue("tier", tier); upsert.Parameters.AddWithValue("margin", percent.Value / 100m);
            upsert.Parameters.AddWithValue("actor", GetActorId(context)); upsert.Parameters.AddWithValue("now", now);
            await upsert.ExecuteNonQueryAsync(context.RequestAborted);
        }
        await WriteEventAsync(connection, transaction, context, "price_suggestion.margins_updated",
            new { request.Price1MarginPercent, request.Price2MarginPercent, request.Price3MarginPercent, costBasis }, now);
        await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new PriceSuggestionMarginSettings(request.Price1MarginPercent, request.Price2MarginPercent,
            request.Price3MarginPercent, costBasis));
    }

    private static async Task<IResult> GetPriceSuggestionsAsync(HttpContext context)
    {
        var invoiceId = RouteGuid(context, "id");
        await using var connection = await OpenAsync(context, context.RequestAborted);
        string? documentNumber;
        await using (var invoice = new NpgsqlCommand($"SELECT document_number FROM {Table("purchase_invoices")} WHERE id=@id", connection))
        {
            invoice.Parameters.AddWithValue("id", invoiceId);
            documentNumber = await invoice.ExecuteScalarAsync(context.RequestAborted) as string;
        }
        if (documentNumber is null) return Results.NotFound(new { error = "purchase_not_found" });

        var costBasis = await ReadPriceSuggestionCostBasisAsync(connection, null, context.RequestAborted);
        var productIds = new List<Guid>();
        await using (var received = new NpgsqlCommand($"SELECT DISTINCT l.product_id FROM {Table("purchase_receipts")} r JOIN {Table("purchase_receipt_lines")} l ON l.receipt_id=r.id WHERE r.invoice_id=@invoice AND r.completed_at_utc IS NOT NULL AND r.reversed_at_utc IS NULL ORDER BY l.product_id", connection))
        {
            received.Parameters.AddWithValue("invoice", invoiceId);
            await using var reader = await received.ExecuteReaderAsync(context.RequestAborted);
            while (await reader.ReadAsync(context.RequestAborted)) productIds.Add(reader.GetGuid(0));
        }
        var marginRates = await ReadPriceSuggestionMarginsAsync(connection, null, context.RequestAborted);
        var missingMargins = new[] { "Price1", "Price2", "Price3" }.Where(tier => !marginRates.ContainsKey(tier)).ToArray();
        if (productIds.Count == 0)
            return Results.Ok(new PriceSuggestionResult(invoiceId, documentNumber, costBasis, [], missingMargins, [], false));

        var catalog = await context.RequestServices.GetRequiredService<IPosProductPricingReadService>()
            .GetSnapshotsAsync(productIds, context.RequestAborted);
        var latestCosts = costBasis == FinancePriceSuggestionCostPolicy.LastReceivedCost
            ? await ReadLatestReceiptCostsAsync(connection, productIds,
                catalog.ToDictionary(product => product.ProductId, product => product.Currency), context.RequestAborted)
            : new Dictionary<Guid, ReceivedCostSource>();
        var suggestions = new List<PriceSuggestionInfo>();
        var unavailable = new List<PriceSuggestionUnavailableInfo>();
        foreach (var product in catalog)
        {
            if (!product.IsActive)
            {
                unavailable.Add(new(product.ProductId, product.Sku, product.Name, "El artículo está inactivo."));
                continue;
            }
            var selectedCost = FinancePriceSuggestionCostPolicy.Choose(costBasis, product.UnitCost,
                latestCosts.GetValueOrDefault(product.ProductId));
            if (selectedCost.UnitCost is null)
            {
                unavailable.Add(new(product.ProductId, product.Sku, product.Name, selectedCost.UnavailableReason!));
                continue;
            }
            if (product.UnitCost is null)
            {
                unavailable.Add(new(product.ProductId, product.Sku, product.Name,
                    "El producto no tiene un costo de inventario inicializado para validar de forma segura el cambio de precio."));
                continue;
            }
            AddSuggestion("Price1", product.Price1);
            AddSuggestion("Price2", product.Price2);
            AddSuggestion("Price3", product.Price3);

            void AddSuggestion(string tier, decimal? currentPrice)
            {
                if (!marginRates.TryGetValue(tier, out var margin)) return;
                var suggested = FinancePurchasePolicy.CalculateSuggestedPrice(selectedCost.UnitCost, margin);
                if (suggested.HasValue)
                    suggestions.Add(new(product.ProductId, product.Sku, product.Name, product.Currency, selectedCost.UnitCost.Value,
                        tier, margin * 100m, currentPrice, suggested.Value, invoiceId, documentNumber, product.UnitCost,
                        selectedCost.Basis, selectedCost.ReceiptSource?.DocumentNumber, selectedCost.ReceiptSource?.ReceivedAtUtc));
            }
        }
        return Results.Ok(new PriceSuggestionResult(invoiceId, documentNumber, costBasis, suggestions, missingMargins, unavailable, true));
    }

    private static async Task<string> ReadPriceSuggestionCostBasisAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT cost_basis FROM {Table("price_suggestion_settings")} WHERE setting_key='default'", connection, transaction);
        var basis = await command.ExecuteScalarAsync(cancellationToken) as string;
        return FinancePriceSuggestionCostPolicy.ResolveBasis(null, basis);
    }

    private static async Task<Dictionary<Guid, ReceivedCostSource>> ReadLatestReceiptCostsAsync(
        NpgsqlConnection connection, IReadOnlyCollection<Guid> productIds,
        IReadOnlyDictionary<Guid, string> productCurrencies, CancellationToken cancellationToken)
    {
        var sql = FinancePriceSuggestionCostPolicy.LatestReceiptCostsSql
            .Replace("{purchase_receipts}", Table("purchase_receipts"), StringComparison.Ordinal)
            .Replace("{purchase_receipt_lines}", Table("purchase_receipt_lines"), StringComparison.Ordinal)
            .Replace("{purchase_invoices}", Table("purchase_invoices"), StringComparison.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("productIds", productIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lines = new List<ReceivedCostLine>();
        while (await reader.ReadAsync(cancellationToken))
            lines.Add(new ReceivedCostLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetDateTime(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetString(6),
                reader.GetDecimal(7)));
        return FinancePriceSuggestionCostPolicy.AggregateLatestReceiptLines(lines, productCurrencies)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static async Task<Dictionary<string, decimal>> ReadPriceSuggestionMarginsAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT price_tier,margin_rate FROM {Table("price_suggestion_margins")} ORDER BY price_tier", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetDecimal(1);
        return result;
    }

    private static decimal? ToMarginPercent(decimal? rate) => rate.HasValue ? rate.Value * 100m : null;
    private static decimal? GetMarginPercent(IReadOnlyDictionary<string, decimal> margins, string tier)
        => margins.TryGetValue(tier, out var rate) ? ToMarginPercent(rate) : null;
    private static bool ValidMarginPercent(decimal? percent) => !percent.HasValue ||
        percent.Value >= 0m && percent.Value < 100m && decimal.Round(percent.Value, 2) == percent.Value;

    private static async Task<IResult> GetFinanceSummaryAsync(HttpContext context)
    {
        await using var connection = await OpenAsync(context, context.RequestAborted);
        await using var command = new NpgsqlCommand($"""
            SELECT i.id,s.legal_name,i.document_type,i.document_number,i.control_number,i.currency,i.issue_date,i.due_date,
                   i.total,COALESCE(sum(p.amount),0)::numeric(18,4),i.status,i.payment_term
            FROM {Table("purchase_invoices")} i
            JOIN {Table("suppliers")} s ON s.id=i.supplier_id
            JOIN {Table("supplier_payables")} p ON p.invoice_id=i.id
            WHERE i.status IN ('Posted','Partial','Paid')
            GROUP BY i.id,s.legal_name
            HAVING sum(p.amount)>0
            ORDER BY i.due_date NULLS LAST,i.issue_date,s.legal_name,i.document_number
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
        var obligations = new List<FinanceOpenPayableInfo>();
        while (await reader.ReadAsync(context.RequestAborted))
        {
            var invoiceAmount = reader.GetDecimal(8);
            var balance = reader.GetDecimal(9);
            obligations.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)), reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)),
                invoiceAmount, invoiceAmount - balance, balance, reader.GetString(10), reader.GetString(11)));
        }
        return Results.Ok(FinanceSummaryPolicy.Build(obligations));
    }

    private static bool ValidatePurchaseDraft(PurchaseDraftRequest request, out string? message)
    {
        message = null;
        if (request.SupplierId == Guid.Empty || request.DocumentType is not ("Invoice" or "DebitNote") ||
            string.IsNullOrWhiteSpace(request.DocumentNumber) || request.DocumentNumber.Trim().Length > 80 ||
            (request.ControlNumber?.Trim().Length ?? 0) > 80 || request.Currency is not ("USD" or "VES") || request.ExchangeRate <= 0m ||
            request.IssueDate == default || request.DueDate.HasValue && request.DueDate.Value < request.IssueDate ||
            request.PaymentTerm is not ("Credit" or "Immediate") || (request.Notes?.Length ?? 0) > 500 ||
            request.Lines is null || request.Lines.Count is < 1 or > 100 || request.Lines.Any(line => line.ProductId == Guid.Empty ||
                line.Quantity <= 0m || line.UnitPrice < 0m || line.Discount < 0m || line.Tax < 0m || line.Discount > line.Quantity * line.UnitPrice) ||
            request.Lines.Select(line => line.ProductId).Distinct().Count() != request.Lines.Count || request.Freight < 0m)
        { message = "Revisa proveedor, documento, moneda, tasa, fechas e importes de las líneas."; return false; }
        try
        {
            var subtotal = request.Lines.Sum(line => FinancePurchasePolicy.Round(line.Quantity * line.UnitPrice));
            var discount = request.Lines.Sum(line => line.Discount);
            var tax = request.Lines.Sum(line => line.Tax);
            var total = FinancePurchasePolicy.CalculateInvoiceTotal(subtotal, discount, request.Freight, tax);
            request.Subtotal = subtotal; request.Discount = discount; request.TaxTotal = tax; request.Total = total;
        }
        catch (ArgumentOutOfRangeException) { message = "Los importes de la factura no son válidos."; return false; }
        return true;
    }

    private static bool ValidatePayment(SupplierPaymentRequest? request, out string? message)
    {
        message = null;
        if (request is null || request.Amount <= 0m || request.Amount != FinancePurchasePolicy.Round(request.Amount) ||
            request.Currency is not ("USD" or "VES") || string.IsNullOrWhiteSpace(request.Method) || request.Method.Trim().Length > 32 ||
            string.IsNullOrWhiteSpace(request.ReceivingAccount) || request.ReceivingAccount.Trim().Length > 100 ||
            (request.Reference?.Length ?? 0) > 100 || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length is < 8 or > 128)
        { message = "Indica importe, moneda, método, cuenta receptora y una clave de operación válida."; return false; }
        return true;
    }

    private static void AddPurchaseHeaderParameters(NpgsqlCommand command, Guid id, PurchaseDraftRequest request,
        string idempotencyKey, string requestHash, HttpContext context)
    {
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("supplier", request.SupplierId); command.Parameters.AddWithValue("type", request.DocumentType);
        command.Parameters.AddWithValue("number", request.DocumentNumber.Trim()); command.Parameters.AddWithValue("control", (object?)TrimOrNull(request.ControlNumber) ?? DBNull.Value);
        command.Parameters.AddWithValue("currency", request.Currency); command.Parameters.AddWithValue("rate", request.ExchangeRate); command.Parameters.AddWithValue("issue", request.IssueDate);
        command.Parameters.AddWithValue("due", (object?)request.DueDate ?? DBNull.Value); command.Parameters.AddWithValue("subtotal", request.Subtotal); command.Parameters.AddWithValue("discount", request.Discount);
        command.Parameters.AddWithValue("freight", request.Freight); command.Parameters.AddWithValue("tax", request.TaxTotal); command.Parameters.AddWithValue("total", request.Total);
        command.Parameters.AddWithValue("term", request.PaymentTerm); command.Parameters.AddWithValue("notes", (object?)TrimOrNull(request.Notes) ?? DBNull.Value);
        command.Parameters.AddWithValue("key", (object?)TrimOrNull(idempotencyKey) ?? DBNull.Value); command.Parameters.AddWithValue("hash", (object?)TrimOrNull(requestHash) ?? DBNull.Value);
        command.Parameters.AddWithValue("actor", GetActorId(context)); command.Parameters.AddWithValue("now", DateTime.UtcNow);
    }

    private static async Task<Dictionary<Guid, ProductSnapshot>> LoadProductsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid[] productIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ProductSnapshot>();
        foreach (var id in productIds)
        {
            await using var command = new NpgsqlCommand("SELECT \"Sku\",\"Name\",\"Active\" FROM products WHERE \"Id\"=@id", connection, transaction);
            command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetBoolean(2)) result[id] = new(reader.GetString(0), reader.GetString(1));
        }
        return result;
    }

    private static async Task<bool> IsActiveSupplierAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid supplierId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {Table("suppliers")} WHERE id=@id AND is_active=TRUE)", connection, transaction);
        command.Parameters.AddWithValue("id", supplierId); return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<string?> ReadInvoiceStatusAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT status FROM {Table("purchase_invoices")} WHERE id=@id{(forUpdate ? " FOR UPDATE" : "")}", connection, transaction);
        command.Parameters.AddWithValue("id", id); return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<PurchaseHeader?> ReadPurchaseHeaderAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid id, CancellationToken ct, bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand($"SELECT supplier_id,document_type,document_number,control_number,currency,exchange_rate,issue_date,due_date,total,freight,status,payment_term FROM {Table("purchase_invoices")} WHERE id=@id{(forUpdate ? " FOR UPDATE" : "")}", connection, transaction);
        command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(id, reader.GetGuid(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), reader.GetString(4), reader.GetDecimal(5),
            DateOnly.FromDateTime(reader.GetDateTime(6)), reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)),
            reader.GetDecimal(8), reader.GetDecimal(9), reader.GetString(10), reader.GetString(11));
    }

    private static async Task<decimal> ReadPayableBalanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid invoiceId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT COALESCE(sum(amount),0)::numeric(18,4) FROM {Table("supplier_payables")} WHERE invoice_id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", invoiceId); return Convert.ToDecimal(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<decimal?> ReadCurrentUsdToVesRateAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(FinanceSql.ReadCurrentUsdToVesRate, connection, transaction);
        command.Parameters.AddWithValue("now", DateTime.UtcNow); var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<PaymentSnapshot?> ReadPaymentForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT p.invoice_id,p.invoice_currency_amount,p.applied_exchange_rate,p.fx_difference,p.reversed_at_utc,(SELECT entry.amount_usd_equivalent FROM {Table("supplier_payables")} entry WHERE entry.payment_id=p.id AND entry.entry_type='Payment' ORDER BY entry.created_at_utc LIMIT 1) FROM {Table("supplier_payments")} p WHERE p.id=@id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(id, reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDecimal(5));
    }

    private static SupplierPaymentInfo ReadPayment(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5),
        reader.IsDBNull(6) ? null : reader.GetDecimal(6), reader.GetString(7), reader.GetString(8), NullableString(reader, 9), reader.GetDateTime(10), reader.IsDBNull(11) ? null : reader.GetDateTime(11));

    private static SupplierInfo ReadSupplier(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
        NullableString(reader, 5), NullableString(reader, 6), reader.GetBoolean(7), reader.GetDateTime(8), reader.GetDateTime(9));

    private static string HashDraft(PurchaseDraftRequest request)
    {
        var canonical = new { request.SupplierId, request.DocumentType, number = request.DocumentNumber.Trim(), control = TrimOrNull(request.ControlNumber), request.Currency,
            request.ExchangeRate, request.IssueDate, request.DueDate, request.Freight, request.PaymentTerm,
            notes = TrimOrNull(request.Notes), lines = request.Lines.OrderBy(line => line.ProductId).Select(line => new { line.ProductId, line.Quantity, line.UnitPrice, line.Discount, line.Tax, line.TaxRecoverable }) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }

    private static string HashReceipt(Guid invoiceId, PurchaseReceiptRequest request)
    {
        var canonical = new { invoiceId, request.WarehouseId, lines = request.Lines.OrderBy(line => line.InvoiceLineId).Select(line => new { line.InvoiceLineId, line.Quantity }) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }

    private static async Task WriteEventAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, HttpContext context,
        string action, object payload, DateTime occurredAtUtc)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {Table("supplier_events")} (id,actor_id,terminal_id,action,payload,occurred_at_utc) VALUES (@id,@actor,@terminal,@action,@payload,@now)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("actor", GetActorId(context)); command.Parameters.AddWithValue("terminal", GetTerminalId(context));
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload)); command.Parameters.AddWithValue("now", occurredAtUtc);
        await command.ExecuteNonQueryAsync(context.RequestAborted);
    }

    private static async Task<NpgsqlConnection> OpenAsync(HttpContext context, CancellationToken ct)
    {
        var connectionString = context.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Pos");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Falta ConnectionStrings:Pos.");
        var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection;
    }

    private static IResult ValidationError(string key, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
    private static bool HasInvalidLength(string? value, int max) => value is not null && value.Trim().Length > max;
    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid RouteGuid(HttpContext context, string key) => Guid.TryParse(Convert.ToString(context.Request.RouteValues[key]), out var id) ? id : Guid.Empty;
    private static Guid GetActorId(HttpContext context) => context.Items["Pos.OperatorId"] is Guid id ? id : Guid.Empty;
    private static Guid GetTerminalId(HttpContext context) => context.Items["Pos.TerminalId"] is Guid id ? id : Guid.Empty;

    public sealed record SupplierRequest(string? Code, string? LegalName, string? TaxId, string? Phone, string? Email, string? Address);
    public sealed record SetActiveRequest(bool Active);
    public sealed record ReverseRequest(string? Reason);
    public sealed record PurchaseLineRequest(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal Discount = 0m, decimal Tax = 0m, bool TaxRecoverable = true);
    public sealed record PurchaseDraftRequest(Guid SupplierId, string DocumentType, string DocumentNumber, string? ControlNumber,
        string Currency, decimal ExchangeRate, DateOnly IssueDate, DateOnly? DueDate, decimal Freight, string PaymentTerm,
        IReadOnlyList<PurchaseLineRequest> Lines, string? IdempotencyKey, string? Notes)
    {
        public decimal Subtotal { get; set; }
        public decimal Discount { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal Total { get; set; }
    }
    public sealed record SupplierPaymentRequest(decimal Amount, string Currency, string Method, string ReceivingAccount,
        string? Reference, string IdempotencyKey);
    public sealed record ImmediatePurchasePostingRequest(SupplierPaymentRequest? Payment);
    public sealed record PriceSuggestionMarginRequest(decimal? Price1MarginPercent, decimal? Price2MarginPercent,
        decimal? Price3MarginPercent, string? CostBasis = null);
    public sealed record PriceSuggestionMarginSettings(decimal? Price1MarginPercent, decimal? Price2MarginPercent,
        decimal? Price3MarginPercent, string CostBasis);
    public sealed record PriceSuggestionResult(Guid InvoiceId, string DocumentNumber, string CostBasis,
        IReadOnlyList<PriceSuggestionInfo> Suggestions,
        IReadOnlyList<string> MissingMarginTiers, IReadOnlyList<PriceSuggestionUnavailableInfo> UnavailableProducts, bool HasReceivedItems);
    public sealed record PriceSuggestionInfo(Guid ProductId, string Sku, string Name, string Currency, decimal UnitCost,
        string PriceTier, decimal MarginPercent, decimal? CurrentPrice, decimal SuggestedPrice, Guid InvoiceId, string DocumentNumber,
        decimal? ProductCostSnapshot, string CostBasis, string? CostSourceDocumentNumber, DateTime? CostReceivedAtUtc);
    public sealed record PriceSuggestionUnavailableInfo(Guid ProductId, string Sku, string Name, string Reason);
    public sealed record SupplierInfo(Guid Id, string Code, string LegalName, string? TaxId, string? Phone, string? Email,
        string? Address, bool IsActive, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
    public sealed record PurchaseSummary(Guid Id, Guid SupplierId, string SupplierName, string DocumentType, string DocumentNumber,
        string? ControlNumber, string Currency, DateOnly IssueDate, DateOnly? DueDate, decimal Total, string Status, string PaymentTerm, decimal Balance);
    public sealed record PurchaseHeader(Guid Id, Guid SupplierId, string DocumentType, string DocumentNumber, string? ControlNumber, string Currency,
        decimal ExchangeRate, DateOnly IssueDate, DateOnly? DueDate, decimal Total, decimal Freight, string Status, string PaymentTerm);
    public sealed record PurchaseLineInfo(Guid Id, Guid ProductId, string Sku, string Name, decimal Quantity, decimal ReceivedQuantity, decimal UnitPrice, decimal Discount, decimal Tax, bool TaxRecoverable, decimal Total);
    public sealed record PurchaseDetail(PurchaseHeader Header, IReadOnlyList<PurchaseLineInfo> Lines);
    public sealed record PurchaseReceiptLineRequest(Guid InvoiceLineId, decimal Quantity);
    public sealed record PurchaseReceiptRequest(string IdempotencyKey, Guid? WarehouseId, IReadOnlyList<PurchaseReceiptLineRequest> Lines);
    public sealed record PurchaseReceiptHeaderInfo(Guid Id, Guid InvoiceId, Guid? WarehouseId, string IdempotencyKey, DateTime CreatedAtUtc, DateTime? CompletedAtUtc, DateTime? ReversedAtUtc);
    public sealed record PurchaseReceiptLineInfo(Guid InvoiceLineId, Guid ProductId, decimal Quantity, decimal UnitCost, string CostCurrency, decimal CostExchangeRate);
    public sealed record PurchaseReceiptInfo(PurchaseReceiptHeaderInfo Header, IReadOnlyList<PurchaseReceiptLineInfo> Lines);
    public sealed record PayableInfo(Guid InvoiceId, Guid SupplierId, string SupplierName, string DocumentType, string DocumentNumber,
        string Currency, DateOnly IssueDate, DateOnly? DueDate, decimal InvoiceAmount, decimal Balance, decimal BalanceUsdEquivalent, decimal ExchangeRate);
    public sealed record SupplierPaymentInfo(Guid Id, Guid InvoiceId, string Currency, decimal Amount, decimal InvoiceCurrencyAmount,
        decimal AppliedExchangeRate, decimal? FxDifference, string Method, string ReceivingAccount, string? Reference, DateTime PaidAtUtc, DateTime? ReversedAtUtc);
    private sealed record ProductSnapshot(string Sku, string Name);
    private sealed record InvoiceReceiptCostLine(Guid InvoiceLineId, Guid ProductId, decimal Quantity, decimal ReceivedQuantity, decimal UnitPrice, decimal Discount, decimal Tax, bool TaxRecoverable, string ProductCurrency);
    private sealed record ReceiptEffectLine(Guid InvoiceLineId, Guid ProductId, decimal Quantity, decimal UnitCost, string CostCurrency);
    private sealed record PaymentSnapshot(Guid Id, Guid InvoiceId, decimal InvoiceCurrencyAmount, decimal AppliedExchangeRate, decimal? FxDifference, DateTime? ReversedAtUtc, decimal? UsdEquivalent);
}
