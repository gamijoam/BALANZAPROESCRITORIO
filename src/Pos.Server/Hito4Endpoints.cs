using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Pos.Domain.Finance;
using Pos.Domain.Identity;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.ProductImages;

internal static class Hito4Endpoints
{
    public static void MapHito4(this WebApplication app)
    {
        app.MapPost("/api/v1/products", async (HttpContext context, PosDbContext db, AuthenticationService auth, CreateProductRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var sku = (request.Sku ?? string.Empty).Trim(); var name = (request.Name ?? string.Empty).Trim();
            var currency = ParseCurrency(request.Currency);
            if (sku.Length is < 1 or > 64 || name.Length is < 2 or > 200 || currency is null || request.UnitPrice < 0m ||
                request.InitialStock < 0m || decimal.Round(request.InitialStock, 3) != request.InitialStock)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["product"] = ["SKU, name, currency, price or opening stock is invalid."] });
            var id = Guid.NewGuid(); var now = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Products.Add(new ProductRecord { Id = id, Sku = sku, Name = name, UnitPrice = request.UnitPrice, Currency = currency, CreatedAtUtc = now });
            db.Stocks.Add(new StockRecord { ProductId = id, Quantity = request.InitialStock });
            if (request.InitialStock > 0m)
                db.StockMovements.Add(new StockMovementRecord { Id = Guid.NewGuid(), ProductId = id, QuantityDelta = request.InitialStock, Reason = "Existencia inicial", ActorId = access.OperatorId, TerminalId = access.TerminalId, OccurredAtUtc = now });
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.created", new { id, sku, name, request.UnitPrice, currency, request.InitialStock }, now));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "sku_already_exists" }); }
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/products/{id}", new { id, sku, name, request.UnitPrice, currency, request.InitialStock, hasImage = false });
        });

        app.MapPost("/api/v1/products/{productId:guid}/image", async (Guid productId, HttpContext context, PosDbContext db,
            AuthenticationService auth, [FromForm] IFormFile file, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId && x.Active, ct);
            if (product is null) return Results.NotFound();
            var store = new ProductImageStorage(ProductImageRoot());
            string fileName;
            try { await using var stream = file.OpenReadStream(); fileName = await store.SaveAsync(productId, stream, file.Length, file.ContentType, ct); }
            catch (InvalidDataException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["image"] = [ex.Message] }); }
            var previous = product.ImageFileName;
            product.ImageFileName = fileName;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.image_changed", new { productId, fileName, file.Length }, DateTime.UtcNow));
            try { await db.SaveChangesAsync(ct); }
            catch { store.Delete(fileName); throw; }
            if (previous is not null && previous != fileName) store.Delete(previous);
            return Results.Ok(new { productId, hasImage = true });
        }).DisableAntiforgery();

        app.MapGet("/api/v1/products/{productId:guid}/image", async (Guid productId, HttpContext context, PosDbContext db,
            AuthenticationService auth, CancellationToken ct) =>
        {
            var access = await GetAccessAsync(context, auth, ct);
            if (access is null) return Results.Unauthorized();
            var fileName = await db.Products.AsNoTracking().Where(x => x.Id == productId && x.Active).Select(x => x.ImageFileName).SingleOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(fileName)) return Results.NotFound();
            var store = new ProductImageStorage(ProductImageRoot());
            var path = store.GetAbsolutePath(fileName);
            if (!File.Exists(path)) return Results.NotFound();
            var mediaType = Path.GetExtension(path).ToLowerInvariant() switch { ".jpg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "application/octet-stream" };
            return Results.File(path, mediaType, enableRangeProcessing: false);
        });

        app.MapPost("/api/v1/products/{productId:guid}/price-changes", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, ChangePriceRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ChangePrice, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var reason = (request.Reason ?? string.Empty).Trim();
            if (request.NewPrice < 0m || decimal.Round(request.NewPrice, 4) != request.NewPrice || reason.Length is < 3 or > 250)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["price"] = ["A nonnegative price (up to four decimals) and reason are required."] });
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId && x.Active, ct);
            if (product is null) return Results.NotFound();
            var oldPrice = product.UnitPrice; var now = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            product.UnitPrice = request.NewPrice;
            var history = new PriceHistoryRecord { Id = Guid.NewGuid(), ProductId = productId, OldPrice = oldPrice, NewPrice = request.NewPrice, Currency = product.Currency, Reason = reason, ActorId = access.OperatorId, ChangedAtUtc = now };
            db.PriceHistory.Add(history);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.price_changed", new { productId, oldPrice, newPrice = request.NewPrice, product.Currency, reason }, now));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { productId, oldPrice, newPrice = product.UnitPrice, product.Currency, reason, history.ChangedAtUtc });
        });
        app.MapPatch("/api/v1/products/{productId:guid}/active", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, SetActiveRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null) return Results.NotFound();
            product.Active = request.Active;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.active_changed", new { productId, request.Active }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { productId, product.Active });
        });

        app.MapGet("/api/v1/products/{productId:guid}/price-history", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ChangePrice, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            return Results.Ok(await db.PriceHistory.AsNoTracking().Where(x => x.ProductId == productId).OrderByDescending(x => x.ChangedAtUtc).Take(100)
                .Select(x => new { x.Id, x.OldPrice, x.NewPrice, x.Currency, x.Reason, x.ActorId, x.ChangedAtUtc }).ToListAsync(ct));
        });

        app.MapGet("/api/v1/exchange-rates", async (HttpContext context, PosDbContext db, AuthenticationService auth, string? from, string? to, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewExchangeRate, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            var query = db.ExchangeRates.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(from)) query = query.Where(x => x.FromCurrency == from.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(to)) query = query.Where(x => x.ToCurrency == to.Trim().ToUpperInvariant());
            return Results.Ok(await query.OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).Take(100)
                .Select(x => new { x.Id, x.FromCurrency, x.ToCurrency, x.Rate, x.EffectiveAtUtc, x.Source, x.ActorId }).ToListAsync(ct));
        });
        app.MapPost("/api/v1/exchange-rates", async (HttpContext context, PosDbContext db, AuthenticationService auth, SetExchangeRateRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageExchangeRate, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var fromCode = ParseCurrency(request.FromCurrency); var toCode = ParseCurrency(request.ToCurrency); var source = (request.Source ?? string.Empty).Trim();
            if (fromCode is null || toCode is null || request.EffectiveAtUtc.Offset != TimeSpan.Zero || source.Length is < 2 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rate"] = ["Currencies, UTC effective time and source are required."] });
            ExchangeRate rate;
            try { rate = ExchangeRate.Create(Enum.Parse<Currency>(fromCode), Enum.Parse<Currency>(toCode), request.Rate, request.EffectiveAtUtc, source); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["rate"] = [ex.Message] }); }
            var row = new ExchangeRateRecord { Id = Guid.NewGuid(), FromCurrency = rate.FromCurrency.ToString(), ToCurrency = rate.ToCurrency.ToString(), Rate = rate.Rate, EffectiveAtUtc = rate.EffectiveAtUtc.UtcDateTime, Source = rate.Source, ActorId = access.OperatorId, CreatedAtUtc = DateTime.UtcNow };
            db.ExchangeRates.Add(row);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "exchange-rate.created", new { row.FromCurrency, row.ToCurrency, row.Rate, row.EffectiveAtUtc, row.Source }, DateTime.UtcNow));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "rate_already_exists_for_effective_time" }); }
            return Results.Created($"/api/v1/exchange-rates/{row.Id}", new { row.Id, row.FromCurrency, row.ToCurrency, row.Rate, row.EffectiveAtUtc, row.Source });
        });
        app.MapGet("/api/v1/exchange-rates/current", async (HttpContext context, PosDbContext db, AuthenticationService auth, string from, string to, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewExchangeRate, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            var fromCode = ParseCurrency(from); var toCode = ParseCurrency(to);
            if (fromCode is null || toCode is null || fromCode == toCode) return Results.BadRequest(new { error = "Two different supported currencies are required." });
            var now = DateTime.UtcNow;
            var current = await db.ExchangeRates.AsNoTracking().Where(x => x.FromCurrency == fromCode && x.ToCurrency == toCode && x.EffectiveAtUtc <= now)
                .OrderByDescending(x => x.EffectiveAtUtc).Select(x => new { x.Id, x.FromCurrency, x.ToCurrency, x.Rate, x.EffectiveAtUtc, x.Source }).FirstOrDefaultAsync(ct);
            return current is null ? Results.NotFound() : Results.Ok(current);
        });

        app.MapGet("/api/v1/customers", async (HttpContext context, PosDbContext db, AuthenticationService auth, string? query, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ManageCustomers, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            var term = (query ?? string.Empty).Trim();
            if (term.Length > 100) return Results.BadRequest(new { error = "Search text is too long." });
            return Results.Ok(await db.Customers.AsNoTracking().Where(x => x.Active &&
                    (term == "" || EF.Functions.ILike(x.Name, "%" + term + "%") || (x.DocumentNumber != null && EF.Functions.ILike(x.DocumentNumber, "%" + term + "%"))))
                .OrderBy(x => x.Name).Take(100).Select(x => new { x.Id, x.Name, x.DocumentNumber, x.Phone, x.Currency, x.CreditLimit }).ToListAsync(ct));
        });
        app.MapPost("/api/v1/customers", async (HttpContext context, PosDbContext db, AuthenticationService auth, CreateCustomerRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCustomers, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var name = (request.Name ?? string.Empty).Trim(); var document = string.IsNullOrWhiteSpace(request.DocumentNumber) ? null : request.DocumentNumber.Trim();
            var currency = ParseCurrency(request.Currency);
            if (name.Length is < 2 or > 200 || document?.Length > 64 || request.Phone?.Trim().Length > 32 || currency is null || request.CreditLimit < 0m)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["customer"] = ["Customer details or credit limit are invalid."] });
            var customer = new CustomerRecord { Id = Guid.NewGuid(), Name = name, DocumentNumber = document, Phone = request.Phone?.Trim(), Currency = currency, CreditLimit = request.CreditLimit, CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "customer.created", new { customer.Id, customer.Name, customer.DocumentNumber, customer.Currency, customer.CreditLimit }, DateTime.UtcNow));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "customer_document_already_exists" }); }
            return Results.Created($"/api/v1/customers/{customer.Id}", new { customer.Id, customer.Name, customer.DocumentNumber, customer.Phone, customer.Currency, customer.CreditLimit });
        });
        app.MapGet("/api/v1/customers/{customerId:guid}/receivables", async (Guid customerId, HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ManageReceivables, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct);
            if (customer is null) return Results.NotFound();
            var entries = await db.ReceivableEntries.AsNoTracking().Where(x => x.CustomerId == customerId).OrderBy(x => x.OccurredAtUtc).ToListAsync(ct);
            var balance = entries.Sum(x => x.EntryType == "Charge" ? x.Amount : -x.Amount);
            return Results.Ok(new { customer.Id, customer.Name, customer.Currency, customer.CreditLimit, balance, entries });
        });
        app.MapPost("/api/v1/customers/{customerId:guid}/payments", async (Guid customerId, HttpContext context, PosDbContext db, AuthenticationService auth, ReceivablePaymentRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageReceivables, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            var reason = (request.Reason ?? string.Empty).Trim();
            if (request.Amount <= 0m || decimal.Round(request.Amount, 4) != request.Amount || reason.Length is < 2 or > 250)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["payment"] = ["A positive payment and reason are required."] });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE \"Id\" = {customerId} AND \"Active\" = TRUE FOR UPDATE").SingleOrDefaultAsync(ct);
            if (customer is null) return Results.NotFound();
            if (request.Currency?.Trim().ToUpperInvariant() != customer.Currency) return Results.UnprocessableEntity(new { error = "currency_mismatch" });
            var balance = await db.ReceivableEntries.Where(x => x.CustomerId == customerId).SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount, ct);
            if (request.Amount > balance) return Results.UnprocessableEntity(new { error = "payment_exceeds_balance" });
            var entry = new ReceivableEntryRecord { Id = Guid.NewGuid(), CustomerId = customerId, EntryType = "Payment", Amount = request.Amount, Currency = customer.Currency, Reason = reason, ActorId = access.OperatorId, TerminalId = access.TerminalId, OccurredAtUtc = DateTime.UtcNow };
            db.ReceivableEntries.Add(entry);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "receivable.payment", new { customerId, entry.Amount, entry.Currency, reason }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/customers/{customerId}/receivables/{entry.Id}", new { entry.Id, entry.Amount, entry.Currency, balance = balance - entry.Amount });
        });

        app.MapPatch("/api/v1/operators/{operatorId:guid}/role", async (Guid operatorId, HttpContext context, PosDbContext db, AuthenticationService auth, ChangeOperatorRoleRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageUsers, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            if (!Enum.TryParse<OperatorRole>(request.Role, false, out var role) || !Enum.IsDefined(role)) return Results.BadRequest(new { error = "Unknown operator role." });
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var target = await db.OperatorUsers.FromSqlInterpolated($"SELECT * FROM operator_users WHERE \"Id\" = {operatorId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (target is null) return Results.NotFound();
            if (target.Active && target.Role == OperatorRole.Administrator.ToString() && role != OperatorRole.Administrator &&
                await db.OperatorUsers.CountAsync(x => x.Active && x.Role == OperatorRole.Administrator.ToString(), ct) <= 1)
                return Results.Conflict(new { error = "at_least_one_active_administrator_required" });
            var oldRole = target.Role; target.Role = role.ToString();
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "operator.role_changed", new { operatorId, oldRole, newRole = target.Role }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { operatorId, role = target.Role });
        });
        app.MapPatch("/api/v1/operators/{operatorId:guid}/active", async (Guid operatorId, HttpContext context, PosDbContext db, AuthenticationService auth, SetActiveRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageUsers, ct);
            if (access is null) return Results.StatusCode(context.Response.StatusCode);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var target = await db.OperatorUsers.FromSqlInterpolated($"SELECT * FROM operator_users WHERE \"Id\" = {operatorId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (target is null) return Results.NotFound();
            if (target.Active && !request.Active && target.Role == OperatorRole.Administrator.ToString() &&
                await db.OperatorUsers.CountAsync(x => x.Active && x.Role == OperatorRole.Administrator.ToString(), ct) <= 1)
                return Results.Conflict(new { error = "at_least_one_active_administrator_required" });
            target.Active = request.Active;
            if (!request.Active)
                await db.AccessSessions.Where(x => x.OperatorId == operatorId && x.RevokedAtUtc == null).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, DateTime.UtcNow), ct);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "operator.active_changed", new { operatorId, request.Active }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { operatorId, target.Active });
        });

        app.MapGet("/api/v1/reports/sales", async (HttpContext context, PosDbContext db, AuthenticationService auth, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92))
                return Results.BadRequest(new { error = "Report range must be UTC, increasing and at most 92 days." });
            var sales = await db.Sales.AsNoTracking().Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc && x.Status == "Confirmed")
                .GroupBy(x => new { Day = x.CreatedAtUtc.Date, x.Currency })
                .Select(x => new { x.Key.Day, x.Key.Currency, Count = x.Count(), Total = x.Sum(y => y.Total) }).OrderBy(x => x.Day).ToListAsync(ct);
            return Results.Ok(new { fromUtc, toUtc, sales });
        });
        app.MapGet("/api/v1/reports/sales/detail", async (HttpContext context, PosDbContext db, AuthenticationService auth,
            DateTime fromUtc, DateTime toUtc, string? currency, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92)) return Results.BadRequest(new { error = "invalid_report_range" });
            var selectedCurrency = string.IsNullOrWhiteSpace(currency) || currency == "Todas" ? null : ParseCurrency(currency);
            if (!string.IsNullOrWhiteSpace(currency) && currency != "Todas" && selectedCurrency is null) return Results.BadRequest(new { error = "invalid_currency" });
            var take = Math.Clamp(pageSize ?? 100, 1, 250); var pageNumber = Math.Max(1, page ?? 1);
            var query = db.Sales.AsNoTracking().Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc && x.Status == "Confirmed");
            if (selectedCurrency is not null) query = query.Where(x => x.Currency == selectedCurrency);
            var totalCount = await query.CountAsync(ct);
            var sales = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Skip((pageNumber - 1) * take).Take(take)
                .Select(x => new { x.Id, x.CreatedAtUtc, x.Total, x.Currency, x.OperatorId, x.CustomerId, x.CashSessionId }).ToListAsync(ct);
            var ids = sales.Select(x => x.Id).ToArray();
            var lines = await db.SaleLines.AsNoTracking().Where(x => ids.Contains(x.SaleId)).ToListAsync(ct);
            var payments = await db.Payments.AsNoTracking().Where(x => ids.Contains(x.SaleId)).ToListAsync(ct);
            var operatorIds = sales.Select(x => x.OperatorId).Distinct().ToArray();
            var users = await db.OperatorUsers.AsNoTracking().Where(x => operatorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, ct);
            var customerIds = sales.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value).Distinct().ToArray();
            var customers = await db.Customers.AsNoTracking().Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return Results.Ok(new { page = pageNumber, pageSize = take, totalCount, sales = sales.Select(s => new {
                s.Id, s.CreatedAtUtc, s.Total, s.Currency, operatorName = users.GetValueOrDefault(s.OperatorId, "—"),
                customerName = s.CustomerId.HasValue ? customers.GetValueOrDefault(s.CustomerId.Value, "Cliente eliminado") : "Venta general",
                s.CashSessionId,
                lines = lines.Where(x => x.SaleId == s.Id).Select(x => new { x.Sku, x.Description, x.Quantity, x.UnitPrice, x.LineTotal, x.OriginalCurrency, x.OriginalUnitPrice }),
                payments = payments.Where(x => x.SaleId == s.Id).Select(x => new { x.Method, x.Amount, x.Currency, x.AppliedExchangeRate })
            }) });
        });
        app.MapGet("/api/v1/reports/cash-sessions", async (HttpContext context, PosDbContext db, AuthenticationService auth,
            DateTime fromUtc, DateTime toUtc, string? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92)) return Results.BadRequest(new { error = "invalid_report_range" });
            var state = string.IsNullOrWhiteSpace(status) || status == "Todas" ? null : status == "Abiertas" ? "Open" : status == "Cerradas" ? "Closed" : status;
            if (state is not null && state is not ("Open" or "Closed")) return Results.BadRequest(new { error = "invalid_status" });
            var take = Math.Clamp(pageSize ?? 100, 1, 250); var pageNumber = Math.Max(1, page ?? 1);
            var query = db.CashSessions.AsNoTracking().Where(x => x.OpenedAtUtc >= fromUtc && x.OpenedAtUtc < toUtc);
            if (state is not null) query = query.Where(x => x.Status == state);
            var totalCount = await query.CountAsync(ct);
            var sessions = await query.OrderByDescending(x => x.OpenedAtUtc).Skip((pageNumber - 1) * take).Take(take).ToListAsync(ct);
            var operatorIds = sessions.Select(x => x.OperatorId).Distinct().ToArray();
            var users = await db.OperatorUsers.AsNoTracking().Where(x => operatorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, ct);
            var sessionIds = sessions.Select(x => x.Id).ToArray();
            var receipts = await (from payment in db.Payments.AsNoTracking() join sale in db.Sales.AsNoTracking() on payment.SaleId equals sale.Id
                                  where sessionIds.Contains(sale.CashSessionId) && (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                                  select new { sale.CashSessionId, payment.Amount, payment.Currency }).ToListAsync(ct);
            var movements = await db.CashMovements.AsNoTracking().Where(x => sessionIds.Contains(x.CashSessionId)).ToListAsync(ct);
            return Results.Ok(new { page = pageNumber, pageSize = take, totalCount, sessions = sessions.Select(x => new {
                x.Id, x.Status, x.Currency, x.OpenedAtUtc, x.ClosedAtUtc, operatorName = users.GetValueOrDefault(x.OperatorId, "—"),
                openingUsd = Opening(x, "USD"), openingVes = Opening(x, "VES"),
                expectedUsd = x.ExpectedClosingBalanceUsd ?? (x.Status == "Open" ? Expected(x, "USD", Opening(x, "USD")) : null), countedUsd = x.CountedClosingBalanceUsd, varianceUsd = x.VarianceUsd,
                expectedVes = x.ExpectedClosingBalanceVes ?? (x.Status == "Open" ? Expected(x, "VES", Opening(x, "VES")) : null), countedVes = x.CountedClosingBalanceVes, varianceVes = x.VarianceVes
            }) });

            static decimal Opening(CashSessionRecord session, string currency)
            {
                if (currency == "USD") return session.OpeningBalanceUsd == 0m && session.Currency == "USD" ? session.OpeningBalance : session.OpeningBalanceUsd;
                return session.OpeningBalanceVes == 0m && session.Currency == "VES" ? session.OpeningBalance : session.OpeningBalanceVes;
            }

            decimal Expected(CashSessionRecord session, string currency, decimal opening)
            {
                return CashSessionLedger.ExpectedBalance(opening,
                    receipts.Where(r => r.CashSessionId == session.Id).Select(r => new CashReceipt(r.Currency, r.Amount)),
                    movements.Where(m => m.CashSessionId == session.Id).Select(m => new CashMovement(m.Currency, m.Direction, m.Amount)), currency);
            }
        });
        app.MapGet("/api/v1/reports/inventory", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return Results.StatusCode(context.Response.StatusCode);
            var rows = await (from product in db.Products.AsNoTracking().Where(x => x.Active)
                              join stock in db.Stocks.AsNoTracking() on product.Id equals stock.ProductId into stockRows
                              from stock in stockRows.DefaultIfEmpty()
                              select new { product.Currency, Quantity = stock == null ? 0m : stock.Quantity, RetailValue = (stock == null ? 0m : stock.Quantity) * product.UnitPrice }).ToListAsync(ct);
            return Results.Ok(rows.GroupBy(x => x.Currency).Select(x => new { currency = x.Key, productCount = x.Count(), units = x.Sum(y => y.Quantity), retailValue = x.Sum(y => y.RetailValue) }));
        });
        app.MapGet("/api/v1/audit", async (HttpContext context, PosDbContext db, AuthenticationService auth, int? limit, CancellationToken ct) =>
        {
            var access = await GetAccessAsync(context, auth, ct);
            if (access is null) return Results.Unauthorized();
            if (!await auth.HasPermissionAsync(access.OperatorId, Permission.ViewSensitiveReports, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var take = Math.Clamp(limit ?? 100, 1, 500);
            return Results.Ok(await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAtUtc).Take(take)
                .Select(x => new { x.Id, x.ActorId, x.TerminalId, x.Action, x.Payload, x.OccurredAtUtc }).ToListAsync(ct));
        });
    }

    private static async Task<AccessSessionRecord?> AuthorizedAsync(HttpContext context, AuthenticationService auth, Permission permission, CancellationToken ct)
    {
        var access = await GetAccessAsync(context, auth, ct);
        if (access is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return null; }
        if (!await auth.HasPermissionAsync(access.OperatorId, permission, ct)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return null; }
        return access;
    }

    private static async Task<AccessSessionRecord?> GetAccessAsync(HttpContext context, AuthenticationService auth, CancellationToken ct)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length <= 7) return null;
        return await auth.FindValidSessionAsync(authorization[7..].Trim(), ct);
    }

    private static string? ParseCurrency(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return Enum.TryParse<Currency>(text, out var currency) && Enum.IsDefined(currency) ? currency.ToString() : null;
    }

    private static string ProductImageRoot()
    {
        var configured = Environment.GetEnvironmentVariable("POS_PRODUCT_IMAGE_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POS") : configured;
    }

    private static AuditRecord Audit(Guid actorId, Guid terminalId, string action, object payload, DateTime occurredAtUtc)
        => new() { Id = Guid.NewGuid(), ActorId = actorId, TerminalId = terminalId, Action = action, Payload = JsonSerializer.Serialize(payload), OccurredAtUtc = occurredAtUtc };
}

public sealed record CreateProductRequest(string? Sku, string? Name, decimal UnitPrice, string? Currency, decimal InitialStock);
public sealed record ChangePriceRequest(decimal NewPrice, string? Reason);
public sealed record SetExchangeRateRequest(string? FromCurrency, string? ToCurrency, decimal Rate, DateTimeOffset EffectiveAtUtc, string? Source);
public sealed record CreateCustomerRequest(string? Name, string? DocumentNumber, string? Phone, string? Currency, decimal CreditLimit);
public sealed record ReceivablePaymentRequest(decimal Amount, string? Currency, string? Reason);
public sealed record SetActiveRequest(bool Active);
public sealed record ChangeOperatorRoleRequest(string? Role);
