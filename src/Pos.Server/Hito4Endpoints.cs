using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Pos.Domain.Finance;
using Pos.Domain.Inventory;
using Pos.Domain.Identity;
using Pos.Application.Reporting;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Modules;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.ProductImages;
using Pos.Infrastructure.Receivables;
using Pos.Infrastructure.Reporting;
using Pos.Shared.Contracts;

internal static class Hito4Endpoints
{
    public static void MapHito4(this WebApplication app)
    {
        app.MapPost("/api/v1/products", async (HttpContext context, PosDbContext db, AuthenticationService auth, CreateProductRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return PermissionFailure(context);
            var sku = (request.Sku ?? string.Empty).Trim(); var name = (request.Name ?? string.Empty).Trim();
            var currency = ParseCurrency(request.Currency);
            Pos.Domain.Catalog.TaxTreatment taxTreatment;
            try { taxTreatment = request.TaxTreatment is null ? Pos.Domain.Catalog.TaxTreatment.Unclassified : Pos.Domain.Catalog.TaxTreatment.FromCode(request.TaxTreatment); }
            catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["taxTreatment"] = ["Selecciona un tratamiento fiscal válido."] }); }
            if (sku.Length is < 1 or > 64 || name.Length is < 2 or > 200 || currency is null || request.UnitPrice < 0m ||
                decimal.Round(request.UnitPrice, 4) != request.UnitPrice || request.Price2 is < 0m || request.Price3 is < 0m ||
                request.Price2 is decimal price2 && decimal.Round(price2, 4) != price2 ||
                request.Price3 is decimal price3 && decimal.Round(price3, 4) != price3 ||
                request.InitialStock < 0m || decimal.Round(request.InitialStock, 3) != request.InitialStock)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["product"] = ["SKU, name, currency, price or opening stock is invalid."] });
            var id = Guid.NewGuid(); var now = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Products.Add(new ProductRecord { Id = id, Sku = sku, Name = name, UnitPrice = request.UnitPrice,
                Price2 = request.Price2, Price3 = request.Price3, TaxTreatment = taxTreatment.Code, Currency = currency, CreatedAtUtc = now });
            db.Stocks.Add(new StockRecord { ProductId = id, Quantity = request.InitialStock });
            if (request.InitialStock > 0m)
                db.StockMovements.Add(new StockMovementRecord { Id = Guid.NewGuid(), ProductId = id, QuantityDelta = request.InitialStock, Reason = "Existencia inicial", ActorId = access.OperatorId, TerminalId = access.TerminalId, OccurredAtUtc = now });
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.created", new { id, sku, name, request.UnitPrice,
                request.Price2, request.Price3, taxTreatment = taxTreatment.Code, currency, request.InitialStock }, now));
            try { await db.SaveChangesAsync(ct); await WarehouseStockBridge.AddProductToWarehousesAsync(db, id, ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "sku_already_exists" }); }
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/products/{id}", new { id, sku, name, request.UnitPrice, request.Price2, request.Price3,
                taxTreatment = taxTreatment.Code, currency, request.InitialStock, hasImage = false });
        });

        app.MapPatch("/api/v1/products/{productId:guid}/tax-treatment", async (Guid productId, HttpContext context, PosDbContext db,
            AuthenticationService auth, ChangeProductTaxTreatmentRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return PermissionFailure(context);
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null) return Results.NotFound();
            Pos.Domain.Catalog.TaxTreatment treatment;
            try { treatment = Pos.Domain.Catalog.TaxTreatment.FromCode(request.TaxTreatment); }
            catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["taxTreatment"] = ["Selecciona un tratamiento fiscal válido."] }); }
            var previous = product.TaxTreatment;
            if (previous == treatment.Code) return Results.Ok(new { productId, taxTreatment = previous, changed = false });
            product.TaxTreatment = treatment.Code;
            var now = DateTime.UtcNow;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.tax_treatment_changed",
                new { productId, previous, current = treatment.Code }, now));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { productId, taxTreatment = treatment.Code, changed = true });
        });

        app.MapPatch("/api/v1/products/{productId:guid}", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, UpdateProductRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return PermissionFailure(context);
            var sku = (request.Sku ?? string.Empty).Trim();
            var name = (request.Name ?? string.Empty).Trim();
            if (sku.Length is < 1 or > 64 || name.Length is < 2 or > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["product"] = ["El SKU debe tener entre 1 y 64 caracteres y el nombre entre 2 y 200."] });
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null) return Results.NotFound();
            var oldSku = product.Sku;
            var oldName = product.Name;
            product.Sku = sku;
            product.Name = name;
            var now = DateTime.UtcNow;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.details_changed", new { productId, oldSku, sku, oldName, name }, now));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "sku_already_exists" }); }
            return Results.Ok(new { productId, product.Sku, product.Name });
        });

        app.MapPost("/api/v1/products/{productId:guid}/image", async (Guid productId, HttpContext context, PosDbContext db,
            AuthenticationService auth, [FromForm] IFormFile file, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return PermissionFailure(context);
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null) return Results.NotFound();
            var store = new ProductImageStorage(ProductImageRoot());
            string fileName;
            try { await using var stream = file.OpenReadStream(); fileName = await store.SaveAsync(productId, stream, file.Length, file.ContentType, ct); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "invalid_product_image" }); }
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
            var fileName = await db.Products.AsNoTracking().Where(x => x.Id == productId).Select(x => x.ImageFileName).SingleOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(fileName)) return Results.NotFound();
            var store = new ProductImageStorage(ProductImageRoot());
            var path = store.GetAbsolutePath(fileName);
            if (!File.Exists(path)) return Results.NotFound();
            var mediaType = Path.GetExtension(path).ToLowerInvariant() switch { ".jpg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "application/octet-stream" };
            return Results.File(path, mediaType, enableRangeProcessing: false);
        });

        app.MapDelete("/api/v1/products/{productId:guid}/image", async (Guid productId, HttpContext context, PosDbContext db,
            AuthenticationService auth, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return PermissionFailure(context);
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null) return Results.NotFound();
            var previous = product.ImageFileName;
            if (string.IsNullOrWhiteSpace(previous)) return Results.NoContent();
            product.ImageFileName = null;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.image_removed", new { productId }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            new ProductImageStorage(ProductImageRoot()).Delete(previous);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/products/{productId:guid}/price-changes", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, ChangePriceRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ChangePrice, ct);
            if (access is null) return PermissionFailure(context);
            var reason = (request.Reason ?? string.Empty).Trim();
            if (request.NewPrice < 0m || decimal.Round(request.NewPrice, 4) != request.NewPrice || reason.Length is < 3 or > 250)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["price"] = ["A nonnegative price (up to four decimals) and reason are required."] });
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId && x.Active, ct);
            if (product is null) return Results.NotFound();
            var priceTier = ParsePriceTier(request.PriceTier);
            if (priceTier is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["priceTier"] = ["Selecciona un nivel de precio válido."] });
            var oldPrice = priceTier switch { "Price1" => product.UnitPrice, "Price2" => product.Price2, _ => product.Price3 };
            if (request.ValidateSnapshot && (oldPrice != request.ExpectedCurrentPrice || product.UnitCost != request.ExpectedUnitCost))
                return Results.Conflict(new { error = "stale_price_suggestion" });
            if (request.ValidateSnapshot && oldPrice == request.NewPrice)
                return Results.Ok(new { productId, oldPrice, newPrice = request.NewPrice, priceTier, product.Currency, unchanged = true });
            var now = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (priceTier == "Price1") product.UnitPrice = request.NewPrice;
            else if (priceTier == "Price2") product.Price2 = request.NewPrice;
            else product.Price3 = request.NewPrice;
            var history = new PriceHistoryRecord { Id = Guid.NewGuid(), ProductId = productId, OldPrice = oldPrice,
                NewPrice = request.NewPrice, Currency = product.Currency, PriceTier = priceTier, Reason = reason, ActorId = access.OperatorId, ChangedAtUtc = now };
            db.PriceHistory.Add(history);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.price_changed", new { productId, priceTier, oldPrice, newPrice = request.NewPrice, product.Currency, reason }, now));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { productId, oldPrice, newPrice = request.NewPrice, priceTier, product.Currency, reason, history.ChangedAtUtc });
        });
        app.MapPost("/api/v1/products/{productId:guid}/initial-cost", async (Guid productId, HttpContext context, PosDbContext db,
            AuthenticationService auth, SetInitialInventoryCostRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.InitializeInventoryCost, ct);
            if (access is null) return PermissionFailure(context);
            var reason = (request.Reason ?? string.Empty).Trim();
            if (request.UnitCost < 0m || decimal.Round(request.UnitCost, 4) != request.UnitCost || reason.Length is < 3 or > 250)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["initialCost"] = ["Indica un costo no negativo con hasta cuatro decimales y un motivo de 3 a 250 caracteres."] });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var product = await db.Products.FromSqlInterpolated($"SELECT * FROM products WHERE \"Id\" = {productId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (product is null || !product.Active) return Results.NotFound();
            if (product.UnitCost.HasValue) return Results.Conflict(new { error = "initial_inventory_cost_already_set" });
            var stock = await db.Stocks.FromSqlInterpolated($"SELECT * FROM stocks WHERE \"ProductId\" = {productId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (stock is null || stock.Quantity <= 0m)
                return Results.Conflict(new { error = "initial_inventory_cost_requires_existing_stock" });

            try { product.UnitCost = InventoryCostPolicy.InitializeMissingCost(product.UnitCost, request.UnitCost); }
            catch (ArgumentOutOfRangeException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["initialCost"] = ["El costo inicial no es válido."] });
            }
            var now = DateTime.UtcNow;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "inventory.initial_cost_set",
                new { productId, product.Sku, unitCost = product.UnitCost, product.Currency, stock.Quantity, reason }, now));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { productId, unitCost = product.UnitCost, product.Currency });
        });
        app.MapPatch("/api/v1/products/{productId:guid}/active", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, SetActiveRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCatalog, ct);
            if (access is null) return PermissionFailure(context);
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null) return Results.NotFound();
            var wasActive = product.Active;
            if (wasActive == request.Active) return Results.Ok(new { productId, product.Active });
            product.Active = request.Active;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "product.active_changed", new { productId, wasActive, active = request.Active }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { productId, product.Active });
        });

        app.MapGet("/api/v1/products/{productId:guid}/price-history", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ChangePrice, ct);
            if (access is null) return PermissionFailure(context);
            return Results.Ok(await db.PriceHistory.AsNoTracking().Where(x => x.ProductId == productId).OrderByDescending(x => x.ChangedAtUtc).Take(100)
                .Select(x => new { x.Id, x.OldPrice, x.NewPrice, x.Currency, x.PriceTier, x.Reason, x.ActorId, x.ChangedAtUtc }).ToListAsync(ct));
        });

        app.MapGet("/api/v1/exchange-rates", async (HttpContext context, PosDbContext db, AuthenticationService auth, string? from, string? to, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewExchangeRate, ct) is null) return PermissionFailure(context);
            var query = db.ExchangeRates.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(from)) query = query.Where(x => x.FromCurrency == from.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(to)) query = query.Where(x => x.ToCurrency == to.Trim().ToUpperInvariant());
            return Results.Ok(await query.OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).Take(100)
                .Select(x => new { x.Id, x.FromCurrency, x.ToCurrency, x.Rate, x.EffectiveAtUtc, x.Source, x.ActorId }).ToListAsync(ct));
        });
        app.MapPost("/api/v1/exchange-rates", async (HttpContext context, PosDbContext db, AuthenticationService auth, SetExchangeRateRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageExchangeRate, ct);
            if (access is null) return PermissionFailure(context);
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
            if (await AuthorizedAsync(context, auth, Permission.ViewExchangeRate, ct) is null) return PermissionFailure(context);
            var fromCode = ParseCurrency(from); var toCode = ParseCurrency(to);
            if (fromCode is null || toCode is null || fromCode == toCode) return Results.BadRequest(new { error = "Two different supported currencies are required." });
            var now = DateTime.UtcNow;
            var current = await db.ExchangeRates.AsNoTracking().Where(x => x.FromCurrency == fromCode && x.ToCurrency == toCode && x.EffectiveAtUtc <= now)
                .OrderByDescending(x => x.EffectiveAtUtc).Select(x => new { x.Id, x.FromCurrency, x.ToCurrency, x.Rate, x.EffectiveAtUtc, x.Source }).FirstOrDefaultAsync(ct);
            return current is null ? Results.NotFound() : Results.Ok(current);
        });

        app.MapGet("/api/v1/customers", async (HttpContext context, PosDbContext db, AuthenticationService auth, string? query, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ManageCustomers, ct) is null) return PermissionFailure(context);
            var term = (query ?? string.Empty).Trim();
            if (term.Length > 100) return Results.BadRequest(new { error = "Search text is too long." });
            var usdToVes = await CurrentUsdToVesRateAsync(db, DateTime.UtcNow, ct);
            var customers = await db.Customers.AsNoTracking().Where(x => x.Active &&
                    (term == "" || EF.Functions.ILike(x.Name, "%" + term + "%") ||
                     (x.DocumentNumber != null && EF.Functions.ILike(x.DocumentNumber, "%" + term + "%")) ||
                     (x.Phone != null && EF.Functions.ILike(x.Phone, "%" + term + "%"))))
                .OrderBy(x => x.Name).Take(100)
                .Select(x => new
                {
                    x.Id, x.Name, x.DocumentNumber, x.Phone, x.Currency, x.CreditLimit,
                    Balance = db.ReceivableEntries.Where(entry => entry.CustomerId == x.Id)
                        .Sum(entry => (decimal?)(entry.EntryType == "Charge" ? entry.Amount : -entry.Amount)) ?? 0m
                }).ToListAsync(ct);
            return Results.Ok(customers.Select(x => new { x.Id, x.Name, x.DocumentNumber, x.Phone, x.Currency, x.CreditLimit, x.Balance,
                AvailableCredit = Math.Max(0m, x.CreditLimit - x.Balance), BalanceVesEquivalent = usdToVes is null ? (decimal?)null : CreditAccountCurrency.ToVes(x.Balance, usdToVes.Value),
                AvailableCreditVesEquivalent = usdToVes is null ? (decimal?)null : CreditAccountCurrency.ToVes(Math.Max(0m, x.CreditLimit - x.Balance), usdToVes.Value), CurrentUsdToVesRate = usdToVes }));
        });
        app.MapPost("/api/v1/customers", async (HttpContext context, PosDbContext db, AuthenticationService auth, CreateCustomerRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCustomers, ct);
            if (access is null) return PermissionFailure(context);
            var name = (request.Name ?? string.Empty).Trim(); var document = string.IsNullOrWhiteSpace(request.DocumentNumber) ? null : request.DocumentNumber.Trim();
            if (name.Length is < 2 or > 200 || document?.Length > 64 || request.Phone?.Trim().Length > 32 || request.CreditLimit < 0m || decimal.Round(request.CreditLimit, 2) != request.CreditLimit)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["customer"] = ["Customer details or credit limit are invalid."] });
            var customer = new CustomerRecord { Id = Guid.NewGuid(), Name = name, DocumentNumber = document, Phone = request.Phone?.Trim(), Currency = "USD", CreditLimit = request.CreditLimit, CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "customer.created", new { customer.Id, customer.Name, customer.DocumentNumber, customer.Currency, customer.CreditLimit }, DateTime.UtcNow));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "customer_document_already_exists" }); }
            return Results.Created($"/api/v1/customers/{customer.Id}", new { customer.Id, customer.Name, customer.DocumentNumber, customer.Phone, customer.Currency, customer.CreditLimit });
        });
        app.MapPatch("/api/v1/customers/{customerId:guid}", async (Guid customerId, HttpContext context, PosDbContext db, AuthenticationService auth, UpdateCustomerRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCustomers, ct);
            if (access is null) return PermissionFailure(context);
            var name = (request.Name ?? string.Empty).Trim();
            var document = string.IsNullOrWhiteSpace(request.DocumentNumber) ? null : request.DocumentNumber.Trim();
            var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
            if (name.Length is < 2 or > 200 || document?.Length > 64 || phone?.Length > 32 || request.CreditLimit < 0m || decimal.Round(request.CreditLimit, 2) != request.CreditLimit)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["customer"] = ["Los datos del cliente o el límite de crédito no son válidos."] });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE \"Id\" = {customerId} AND \"Active\" = TRUE FOR UPDATE").SingleOrDefaultAsync(ct);
            if (customer is null) return Results.NotFound();
            var balance = await db.ReceivableEntries.Where(x => x.CustomerId == customerId)
                .SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount, ct);
            if (request.CreditLimit < balance)
                return Results.Conflict(new { error = "credit_limit_below_current_balance", balance });
            var before = new { customer.Name, customer.DocumentNumber, customer.Phone, customer.CreditLimit };
            customer.Name = name;
            customer.DocumentNumber = document;
            customer.Phone = phone;
            customer.CreditLimit = request.CreditLimit;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "customer.updated", new { customerId, before, after = new { customer.Name, customer.DocumentNumber, customer.Phone, customer.CreditLimit } }, DateTime.UtcNow));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            { return Results.Conflict(new { error = "customer_document_already_exists" }); }
            await tx.CommitAsync(ct);
            return Results.Ok(new { customer.Id, customer.Name, customer.DocumentNumber, customer.Phone, customer.Currency, customer.CreditLimit, Balance = balance });
        });
        app.MapPatch("/api/v1/customers/{customerId:guid}/active", async (Guid customerId, HttpContext context, PosDbContext db, AuthenticationService auth, SetActiveRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageCustomers, ct);
            if (access is null) return PermissionFailure(context);
            if (request.Active) return Results.BadRequest(new { error = "customer_reactivation_not_supported" });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE \"Id\" = {customerId} AND \"Active\" = TRUE FOR UPDATE").SingleOrDefaultAsync(ct);
            if (customer is null) return Results.NotFound();
            var balance = await db.ReceivableEntries.Where(x => x.CustomerId == customerId)
                .SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount, ct);
            if (balance > 0m) return Results.Conflict(new { error = "customer_has_balance", balance });
            customer.Active = false;
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "customer.deactivated", new { customerId, customer.Name, balance, historyPreserved = true }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/customers/{customerId:guid}/receivables", async (Guid customerId, HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ManageReceivables, ct) is null) return PermissionFailure(context);
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct);
            if (customer is null) return Results.NotFound();
            var entries = await db.ReceivableEntries.AsNoTracking().Where(x => x.CustomerId == customerId).OrderBy(x => x.OccurredAtUtc).ToListAsync(ct);
            var balance = entries.Sum(x => x.EntryType == "Charge" ? x.Amount : -x.Amount);
            var usdToVes = await CurrentUsdToVesRateAsync(db, DateTime.UtcNow, ct);
            var saleIds = entries.Where(x => x.EntryType == "Charge" && x.SaleId.HasValue).Select(x => x.SaleId!.Value).Distinct().ToArray();
            var sales = saleIds.Length == 0
                ? new Dictionary<Guid, SaleRecord>()
                : await db.Sales.AsNoTracking().Where(x => saleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var saleLines = saleIds.Length == 0
                ? []
                : await db.SaleLines.AsNoTracking().Where(x => saleIds.Contains(x.SaleId)).OrderBy(x => x.Description).ToListAsync(ct);
            var saleItems = saleLines.GroupBy(x => x.SaleId).ToDictionary(group => group.Key, group => group.Select(line =>
            {
                var sale = sales.GetValueOrDefault(group.Key);
                decimal? amountUsd = sale?.Currency == "USD"
                    ? line.LineTotal
                    : sale?.AppliedUsdToVesRate is decimal rate && rate > 0m
                        ? decimal.Round(line.LineTotal / rate, 2, MidpointRounding.AwayFromZero)
                        : null;
                return new { line.Sku, line.Description, line.Quantity, AmountUsd = amountUsd };
            }).ToArray());
            var entriesWithSaleDetails = entries.Select(entry => new
            {
                entry.Id, entry.CustomerId, entry.SaleId, entry.EntryType, entry.Amount, entry.Currency,
                entry.Reason, entry.IsOpeningBalance, entry.ActorId, entry.TerminalId, entry.OccurredAtUtc, entry.OriginalAmount,
                entry.OriginalCurrency, entry.AppliedExchangeRate,
                saleItems = entry.SaleId is Guid saleId && saleItems.TryGetValue(saleId, out var items) ? items : []
            }).ToArray();
            return Results.Ok(new { customer.Id, customer.Name, currency = "USD", customer.CreditLimit, balance,
                balanceVesEquivalent = usdToVes is null ? (decimal?)null : CreditAccountCurrency.ToVes(balance, usdToVes.Value),
                currentUsdToVesRate = usdToVes, entries = entriesWithSaleDetails });
        });
        app.MapPost("/api/v1/customers/{customerId:guid}/payments", async (Guid customerId, HttpContext context, PosDbContext db, AuthenticationService auth, ReceivablePaymentRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageReceivables, ct);
            if (access is null) return PermissionFailure(context);
            var reference = (request.Reference ?? request.Reason ?? string.Empty).Trim();
            if (request.Amount <= 0m || decimal.Round(request.Amount, 2) != request.Amount || reference.Length > 250)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["payment"] = ["El monto del abono debe ser positivo y la referencia no puede superar 250 caracteres."] });
            var paymentCurrency = request.Currency?.Trim().ToUpperInvariant();
            if (paymentCurrency is not ("USD" or "VES")) return Results.UnprocessableEntity(new { error = "unsupported_currency" });
            var usdToVes = paymentCurrency == "VES" ? await CurrentUsdToVesRateAsync(db, DateTime.UtcNow, ct) : null;
            if (paymentCurrency == "VES" && usdToVes is null) return Results.UnprocessableEntity(new { error = "exchange_rate_unavailable" });
            var paymentUsd = CreditAccountCurrency.ToUsd(request.Amount, paymentCurrency, usdToVes ?? 1m);
            if (paymentUsd <= 0m) return Results.UnprocessableEntity(new { error = "payment_below_minimum_usd" });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE \"Id\" = {customerId} AND \"Active\" = TRUE FOR UPDATE").SingleOrDefaultAsync(ct);
            if (customer is null) return Results.NotFound();
            if (customer.Currency != "USD") return Results.UnprocessableEntity(new { error = "customer_account_currency_invalid" });
            var balance = await db.ReceivableEntries.Where(x => x.CustomerId == customerId).SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount, ct);
            if (paymentUsd > balance) return Results.UnprocessableEntity(new { error = "payment_exceeds_balance" });
            var entry = new ReceivableEntryRecord { Id = Guid.NewGuid(), CustomerId = customerId, EntryType = "Payment", Amount = paymentUsd, Currency = "USD", OriginalAmount = request.Amount, OriginalCurrency = paymentCurrency, AppliedExchangeRate = paymentCurrency == "USD" ? null : CreditAccountCurrency.UsdPerVes(usdToVes!.Value), Reason = reference, ActorId = access.OperatorId, TerminalId = access.TerminalId, OccurredAtUtc = DateTime.UtcNow };
            db.ReceivableEntries.Add(entry);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "receivable.payment", new { customerId, originalAmount = request.Amount, originalCurrency = paymentCurrency, amountUsd = paymentUsd, appliedExchangeRate = entry.AppliedExchangeRate, reference }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/customers/{customerId}/receivables/{entry.Id}", new { entry.Id, entry.Amount, entry.Currency, entry.OriginalAmount, entry.OriginalCurrency, entry.AppliedExchangeRate, balance = balance - entry.Amount });
        });

        app.MapPost("/api/v1/customers/{customerId:guid}/receivables/opening-balance", async (Guid customerId, HttpContext context,
            PosDbContext db, AuthenticationService auth, RecordReceivableOpeningBalanceRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageReceivables, ct);
            if (access is null) return PermissionFailure(context);
            var reason = (request.Reason ?? string.Empty).Trim();
            var key = (request.IdempotencyKey ?? string.Empty).Trim();
            if (request.AmountUsd <= 0m || decimal.Round(request.AmountUsd, 4, MidpointRounding.AwayFromZero) != request.AmountUsd ||
                reason.Length is < 3 or > 250 || key.Length is < 1 or > 128)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["openingBalance"] = ["El saldo debe ser positivo, usar máximo cuatro decimales e incluir un motivo y una clave de carga."] });

            try
            {
                var result = await new ReceivableOpeningBalanceService(db).RecordAsync(customerId, request.AmountUsd, reason,
                    key, access.OperatorId, access.TerminalId, ct);
                var response = new { id = result.EntryId, amount = result.Amount, currency = "USD", replayed = result.Replayed };
                return result.Replayed
                    ? Results.Ok(response)
                    : Results.Created($"/api/v1/customers/{customerId}/receivables/{result.EntryId}", response);
            }
            catch (ReceivableOpeningBalanceException ex) when (ex.Code == "customer_not_found")
            { return Results.NotFound(); }
            catch (ReceivableOpeningBalanceException ex) when (ex.Code is "opening_balance_already_exists" or "opening_balance_requires_empty_account" or "idempotency_key_reused")
            { return Results.Conflict(new { error = ex.Code }); }
            catch (ReceivableOpeningBalanceException ex)
            { return Results.UnprocessableEntity(new { error = ex.Code }); }
        });

        app.MapPatch("/api/v1/operators/{operatorId:guid}/role", async (Guid operatorId, HttpContext context, PosDbContext db, AuthenticationService auth, ChangeOperatorRoleRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageUsers, ct);
            if (access is null) return PermissionFailure(context);
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
            if (access is null) return PermissionFailure(context);
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
        app.MapGet("/api/v1/operators/{operatorId:guid}/permissions", async (Guid operatorId, HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ManageUsers, ct) is null) return PermissionFailure(context);
            var target = await db.OperatorUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operatorId, ct);
            if (target is null) return Results.NotFound();
            var overrides = await auth.GetPermissionOverridesAsync(operatorId, ct);
            return Results.Ok(new { role = target.Role, permissions = Enum.GetValues<Permission>().Select(permission => new
            {
                permission = permission.ToString(),
                mode = target.Role == OperatorRole.Administrator.ToString() ? PermissionOverrideMode.Allow.ToString() :
                    overrides.GetValueOrDefault(permission, PermissionOverrideMode.Inherit).ToString(),
                effective = (target.Role == OperatorRole.Administrator.ToString() ? PermissionAccessMode.Allow :
                    OperatorPermissionPolicy.Resolve(Enum.Parse<OperatorRole>(target.Role), permission,
                        overrides.GetValueOrDefault(permission, PermissionOverrideMode.Inherit))).ToString()
            }) });
        });
        app.MapPut("/api/v1/operators/{operatorId:guid}/permissions", async (Guid operatorId, HttpContext context, PosDbContext db, AuthenticationService auth, SetOperatorPermissionsRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ManageUsers, ct);
            if (access is null) return PermissionFailure(context);
            var target = await db.OperatorUsers.SingleOrDefaultAsync(x => x.Id == operatorId, ct);
            if (target is null) return Results.NotFound();
            if (target.Role == OperatorRole.Administrator.ToString()) return Results.Conflict(new { error = "administrator_permissions_are_fixed" });
            if (request.Permissions is null || request.Permissions.Count != Enum.GetValues<Permission>().Length ||
                request.Permissions.Select(x => x.Permission).Distinct(StringComparer.Ordinal).Count() != request.Permissions.Count ||
                request.Permissions.Any(x => !Enum.TryParse<Permission>(x.Permission, false, out var permission) || !Enum.IsDefined(permission) ||
                    !Enum.TryParse<PermissionOverrideMode>(x.Mode, false, out var mode) || !Enum.IsDefined(mode)) ||
                request.Permissions.Any(x => x.Permission == Permission.ManageUsers.ToString() && x.Mode == PermissionOverrideMode.Allow.ToString()))
                return Results.BadRequest(new { error = "invalid_permission_matrix" });
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            await db.OperatorPermissionOverrides.Where(x => x.OperatorId == operatorId).ExecuteDeleteAsync(ct);
            var rows = request.Permissions.Where(x => x.Mode != PermissionOverrideMode.Inherit.ToString()).Select(x => new OperatorPermissionOverrideRecord
            {
                Id = Guid.NewGuid(), OperatorId = operatorId, Permission = x.Permission, Mode = x.Mode,
                UpdatedByOperatorId = access.OperatorId, UpdatedAtUtc = DateTime.UtcNow
            }).ToArray();
            db.OperatorPermissionOverrides.AddRange(rows);
            db.AuditEvents.Add(Audit(access.OperatorId, access.TerminalId, "operator.permissions_changed", new { operatorId, permissions = request.Permissions }, DateTime.UtcNow));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { operatorId, updated = request.Permissions.Count });
        });

        app.MapGet("/api/v1/reports/receivables/balances", async (HttpContext context, PosDbContext db,
            AuthenticationService auth, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            var result = await new ReceivablesReportQueryService(db)
                .GetBalancePageAsync(page ?? 1, pageSize ?? 100, ct);
            return Results.Ok(result);
        });
        app.MapGet("/api/v1/reports/receivables/movements", async (HttpContext context, PosDbContext db,
            AuthenticationService auth, DateTime fromUtc, DateTime toUtc, string? currency, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc ||
                toUtc - fromUtc > TimeSpan.FromDays(92))
                return Results.BadRequest(new { error = "invalid_report_range" });
            var selectedCurrency = string.IsNullOrWhiteSpace(currency) || currency.Equals("Todas", StringComparison.OrdinalIgnoreCase)
                ? null : currency.Trim().ToUpperInvariant();
            if (selectedCurrency is not (null or "USD" or "VES"))
                return Results.BadRequest(new { error = "invalid_currency" });
            var result = await new ReceivablesReportQueryService(db)
                .GetMovementPageAsync(fromUtc, toUtc, selectedCurrency, page ?? 1, pageSize ?? 100, ct);
            return Results.Ok(result);
        });

        app.MapGet("/api/v1/reports/sales", async (HttpContext context, PosDbContext db, AuthenticationService auth, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92))
                return Results.BadRequest(new { error = "Report range must be UTC, increasing and at most 92 days." });
            var netSales = db.Sales.AsNoTracking()
                .Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc && x.Status != "Voided")
                .Select(x => new
                {
                    Day = x.CreatedAtUtc.Date, x.Currency,
                    Total = x.Total - (db.SaleReturns.Where(r => r.SaleId == x.Id).Sum(r => (decimal?)r.Amount) ?? 0m)
                });
            var sales = await netSales.GroupBy(x => new { x.Day, x.Currency })
                .Select(x => new { x.Key.Day, x.Key.Currency, Count = x.Count(), Total = x.Sum(y => y.Total) }).OrderBy(x => x.Day).ToListAsync(ct);
            return Results.Ok(new { fromUtc, toUtc, sales });
        });
        app.MapGet("/api/v1/reports/products/sales", async (HttpContext context, PosDbContext db, AuthenticationService auth,
            DateTime fromUtc, DateTime toUtc, string? currency, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92))
                return Results.BadRequest(new { error = "invalid_report_range" });
            var selectedCurrency = string.IsNullOrWhiteSpace(currency) || currency == "Todas" ? null : ParseCurrency(currency);
            if (!string.IsNullOrWhiteSpace(currency) && currency != "Todas" && selectedCurrency is null)
                return Results.BadRequest(new { error = "invalid_currency" });

            var periodSales = db.Sales.AsNoTracking().Where(sale => sale.CreatedAtUtc >= fromUtc && sale.CreatedAtUtc < toUtc && sale.Status != "Voided");
            if (selectedCurrency is not null) periodSales = periodSales.Where(sale => sale.Currency == selectedCurrency);
            var sold = await (from sale in periodSales
                              join line in db.SaleLines.AsNoTracking() on sale.Id equals line.SaleId
                              group line by new { line.ProductId, line.Sku, Name = line.Description, sale.Currency } into product
                              select new
                              {
                                  product.Key.ProductId, product.Key.Sku, product.Key.Name, product.Key.Currency,
                                  Quantity = product.Sum(line => line.Quantity), Amount = product.Sum(line => line.LineTotal)
                              }).ToListAsync(ct);
            var returned = await (from sale in periodSales
                                  join line in db.SaleLines.AsNoTracking() on sale.Id equals line.SaleId
                                  join returnLine in db.SaleReturnLines.AsNoTracking() on line.Id equals returnLine.SaleLineId
                                  group returnLine by new { line.ProductId, line.Sku, Name = line.Description, sale.Currency } into product
                                  select new
                                  {
                                      product.Key.ProductId, product.Key.Sku, product.Key.Name, product.Key.Currency,
                                      Quantity = product.Sum(line => line.Quantity), Amount = product.Sum(line => line.Amount)
                                  }).ToListAsync(ct);
            var returnedByProduct = returned.ToDictionary(x => (x.ProductId, x.Sku, x.Name, x.Currency));
            var products = sold.Select(x =>
            {
                var returnedRow = returnedByProduct.GetValueOrDefault((x.ProductId, x.Sku, x.Name, x.Currency));
                var returnedQuantity = returnedRow?.Quantity ?? 0m;
                var returnedAmount = returnedRow?.Amount ?? 0m;
                return new
                {
                    x.ProductId, x.Sku, x.Name, x.Currency, SoldQuantity = x.Quantity,
                    ReturnedQuantity = returnedQuantity, NetQuantity = x.Quantity - returnedQuantity,
                    GrossAmount = x.Amount, ReturnedAmount = returnedAmount, NetAmount = x.Amount - returnedAmount
                };
            }).OrderByDescending(x => x.NetAmount).ThenBy(x => x.Name).ToArray();
            return Results.Ok(new { fromUtc, toUtc, currency = selectedCurrency ?? "Todas", products });
        });
        app.MapGet("/api/v1/reports/sales/detail", async (HttpContext context, PosDbContext db, AuthenticationService auth,
            DateTime fromUtc, DateTime toUtc, string? currency, int? page, int? pageSize, string? paymentMethod,
            string? operatorName, string? status, string? query, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92)) return Results.BadRequest(new { error = "invalid_report_range" });
            var selectedCurrency = string.IsNullOrWhiteSpace(currency) || currency == "Todas" ? null : ParseCurrency(currency);
            if (!string.IsNullOrWhiteSpace(currency) && currency != "Todas" && selectedCurrency is null) return Results.BadRequest(new { error = "invalid_currency" });
            var methodFilter = paymentMethod?.Trim();
            var operatorFilter = operatorName?.Trim();
            var selectedStatus = status switch
            {
                null or "" or "Todas" => null,
                "Confirmada" or "Confirmed" => "Confirmed",
                "Devuelta parcialmente" or "PartiallyReturned" => "PartiallyReturned",
                "Devuelta" or "Returned" => "Returned",
                _ => "invalid"
            };
            var searchTerm = query?.Trim();
            if (methodFilter?.Length > 64 || operatorFilter?.Length > 100 || searchTerm?.Length > 100) return Results.BadRequest(new { error = "invalid_sales_filter" });
            if (selectedStatus == "invalid") return Results.BadRequest(new { error = "invalid_status" });
            var take = Math.Clamp(pageSize ?? 100, 1, 250); var pageNumber = Math.Max(1, page ?? 1);
            var salesQuery = db.Sales.AsNoTracking().Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc && x.Status != "Voided");
            if (selectedCurrency is not null) salesQuery = salesQuery.Where(x => x.Currency == selectedCurrency);
            if (!string.IsNullOrWhiteSpace(methodFilter))
                salesQuery = salesQuery.Where(sale => db.Payments.Any(payment => payment.SaleId == sale.Id && payment.Method.ToLower() == methodFilter.ToLower()));
            if (!string.IsNullOrWhiteSpace(operatorFilter))
            {
                var matchingOperators = await db.OperatorUsers.AsNoTracking()
                    .Where(user => EF.Functions.ILike(user.Username, $"%{operatorFilter}%"))
                    .Select(user => user.Id).ToArrayAsync(ct);
                salesQuery = salesQuery.Where(sale => matchingOperators.Contains(sale.OperatorId));
            }
            if (selectedStatus is not null) salesQuery = salesQuery.Where(x => x.Status == selectedStatus);
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var pattern = $"%{searchTerm}%";
                var salesSearch = await db.Sales.AsNoTracking()
                    .Where(sale => sale.CreatedAtUtc >= fromUtc && sale.CreatedAtUtc < toUtc && sale.Status != "Voided" && EF.Functions.ILike(sale.Id.ToString(), pattern))
                    .Select(sale => sale.Id).ToArrayAsync(ct);
                salesQuery = salesQuery.Where(sale => salesSearch.Contains(sale.Id) ||
                    (sale.CustomerId.HasValue && db.Customers.Any(customer => customer.Id == sale.CustomerId.Value && EF.Functions.ILike(customer.Name, pattern))) ||
                    db.OperatorUsers.Any(user => user.Id == sale.OperatorId && EF.Functions.ILike(user.Username, pattern)) ||
                    db.SaleLines.Any(line => line.SaleId == sale.Id && (EF.Functions.ILike(line.Sku, pattern) || EF.Functions.ILike(line.Description, pattern))));
            }
            var totalCount = await salesQuery.CountAsync(ct);
            var sales = await salesQuery.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Skip((pageNumber - 1) * take).Take(take)
                .Select(x => new { x.Id, x.CreatedAtUtc, x.Total, x.RoundingAdjustment, x.AppliedUsdToVesRate, x.Currency, x.Status, x.OperatorId, x.CustomerId, x.CashSessionId }).ToListAsync(ct);
            var ids = sales.Select(x => x.Id).ToArray();
            var lines = await db.SaleLines.AsNoTracking().Where(x => ids.Contains(x.SaleId)).ToListAsync(ct);
            var returns = await db.SaleReturns.AsNoTracking().Where(x => ids.Contains(x.SaleId)).ToListAsync(ct);
            var returnedLines = await (from line in db.SaleReturnLines.AsNoTracking()
                                       join header in db.SaleReturns.AsNoTracking() on line.SaleReturnId equals header.Id
                                       where ids.Contains(header.SaleId)
                                       select new { header.SaleId, line.SaleLineId, line.Quantity }).ToListAsync(ct);
            var payments = await db.Payments.AsNoTracking().Where(x => ids.Contains(x.SaleId)).ToListAsync(ct);
            var operatorIds = sales.Select(x => x.OperatorId).Distinct().ToArray();
            var users = await db.OperatorUsers.AsNoTracking().Where(x => operatorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, ct);
            var customerIds = sales.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value).Distinct().ToArray();
            var customers = await db.Customers.AsNoTracking().Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return Results.Ok(new { page = pageNumber, pageSize = take, totalCount, sales = sales.Select(s => new {
                s.Id, s.CreatedAtUtc, s.Total, s.RoundingAdjustment, s.AppliedUsdToVesRate, s.Currency, s.Status,
                returnedAmount = returns.Where(x => x.SaleId == s.Id).Sum(x => x.Amount),
                operatorName = users.GetValueOrDefault(s.OperatorId, "—"),
                customerName = s.CustomerId.HasValue ? customers.GetValueOrDefault(s.CustomerId.Value, "Cliente eliminado") : "Venta general",
                s.CashSessionId,
                lines = lines.Where(x => x.SaleId == s.Id).Select(x => new
                {
                    x.Id, x.Sku, x.Description, x.Quantity, x.UnitPrice, x.LineTotal, x.OriginalCurrency, x.OriginalUnitPrice,
                    x.PriceTier, x.TaxTreatment, x.TaxRate, x.TaxAmount,
                    returnedQuantity = returnedLines.Where(r => r.SaleLineId == x.Id).Sum(r => r.Quantity),
                    returnableQuantity = x.Quantity - returnedLines.Where(r => r.SaleLineId == x.Id).Sum(r => r.Quantity)
                }),
                payments = payments.Where(x => x.SaleId == s.Id).Select(x => new { x.Method, x.Amount, x.Currency, x.AppliedExchangeRate })
            }) });
        });
        app.MapGet("/api/v1/reports/cash-sessions", async (HttpContext context, PosDbContext db, AuthenticationService auth,
            DateTime fromUtc, DateTime toUtc, string? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
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
                                  where sessionIds.Contains(sale.CashSessionId) && sale.Status != "Voided"
                                  select new { sale.CashSessionId, payment.Method, payment.Amount, payment.Currency }).ToListAsync(ct);
            var refunds = await (from refund in db.SaleReturnPayments.AsNoTracking()
                                 join saleReturn in db.SaleReturns.AsNoTracking() on refund.SaleReturnId equals saleReturn.Id
                                 where sessionIds.Contains(saleReturn.CashSessionId)
                                 select new { saleReturn.CashSessionId, refund.Method, refund.Amount, refund.Currency }).ToListAsync(ct);
            var movements = await db.CashMovements.AsNoTracking().Where(x => sessionIds.Contains(x.CashSessionId)).ToListAsync(ct);
            return Results.Ok(new { page = pageNumber, pageSize = take, totalCount, sessions = sessions.Select(x => new {
                x.Id, x.Status, x.Currency, x.OpenedAtUtc, x.ClosedAtUtc, operatorName = users.GetValueOrDefault(x.OperatorId, "—"),
                openingUsd = Opening(x, "USD"), openingVes = Opening(x, "VES"),
                expectedUsd = x.ExpectedClosingBalanceUsd ?? (x.Status == "Open" ? Expected(x, "USD", Opening(x, "USD")) : null), countedUsd = x.CountedClosingBalanceUsd, varianceUsd = x.VarianceUsd,
                expectedVes = x.ExpectedClosingBalanceVes ?? (x.Status == "Open" ? Expected(x, "VES", Opening(x, "VES")) : null), countedVes = x.CountedClosingBalanceVes, varianceVes = x.VarianceVes,
                payments = receipts.Where(r => r.CashSessionId == x.Id).GroupBy(r => new { r.Method, r.Currency })
                    .Select(group => new { operation = "Cobro", method = group.Key.Method, currency = group.Key.Currency, amount = group.Sum(item => item.Amount) })
                    .Concat(refunds.Where(r => r.CashSessionId == x.Id).GroupBy(r => new { r.Method, r.Currency })
                        .Select(group => new { operation = "Reintegro", method = group.Key.Method, currency = group.Key.Currency, amount = group.Sum(item => item.Amount) }))
                    .OrderBy(payment => payment.operation).ThenBy(payment => payment.method).ThenBy(payment => payment.currency).ToArray(),
                movements = movements.Where(movement => movement.CashSessionId == x.Id)
                    .Select(movement => new { direction = movement.Direction, currency = movement.Currency, amount = movement.Amount, reason = movement.Reason, occurredAtUtc = movement.OccurredAtUtc }).ToArray()
            }) });

            static decimal Opening(CashSessionRecord session, string currency)
            {
                if (currency == "USD") return session.OpeningBalanceUsd == 0m && session.Currency == "USD" ? session.OpeningBalance : session.OpeningBalanceUsd;
                return session.OpeningBalanceVes == 0m && session.Currency == "VES" ? session.OpeningBalance : session.OpeningBalanceVes;
            }

            decimal Expected(CashSessionRecord session, string currency, decimal opening)
            {
                return CashSessionLedger.ExpectedBalance(opening,
                    receipts.Where(r => r.CashSessionId == session.Id && (r.Method.Equals("cash", StringComparison.OrdinalIgnoreCase) || r.Method.Equals("efectivo", StringComparison.OrdinalIgnoreCase)))
                        .Select(r => new CashReceipt(r.Currency, r.Amount)),
                    movements.Where(m => m.CashSessionId == session.Id).Select(m => new CashMovement(m.Currency, m.Direction, m.Amount)), currency);
            }
        });
        app.MapGet("/api/v1/reports/inventory", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            var rows = await (from product in db.Products.AsNoTracking().Where(x => x.Active)
                              join stock in db.Stocks.AsNoTracking() on product.Id equals stock.ProductId into stockRows
                              from stock in stockRows.DefaultIfEmpty()
                              select new { product.Currency, Quantity = stock == null ? 0m : stock.Quantity, RetailValue = (stock == null ? 0m : stock.Quantity) * product.UnitPrice }).ToListAsync(ct);
            return Results.Ok(rows.GroupBy(x => x.Currency).Select(x => new { currency = x.Key, productCount = x.Count(), units = x.Sum(y => y.Quantity), retailValue = x.Sum(y => y.RetailValue) }));
        });
        app.MapGet("/api/v1/reports/inventory/movements", async (HttpContext context, PosDbContext db,
            AuthenticationService auth, DateTime fromUtc, DateTime toUtc, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (await AuthorizedAsync(context, auth, Permission.ViewReports, ct) is null) return PermissionFailure(context);
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(92))
                return Results.BadRequest(new { error = "invalid_report_range" });
            var pageNumber = Math.Max(1, page ?? 1); var take = Math.Clamp(pageSize ?? 100, 1, 250);
            var rows = await (from movement in db.StockMovements.AsNoTracking()
                              join product in db.Products.AsNoTracking() on movement.ProductId equals product.Id
                              join actorRow in db.OperatorUsers.AsNoTracking() on movement.ActorId equals actorRow.Id into actors
                              from actor in actors.DefaultIfEmpty()
                              where movement.OccurredAtUtc >= fromUtc && movement.OccurredAtUtc < toUtc
                              orderby movement.OccurredAtUtc descending, movement.Id descending
                              select new { movement.Id, movement.OccurredAtUtc, product.Sku, ProductName = product.Name,
                                  movement.QuantityDelta, movement.Reason, Actor = actor == null ? "—" : actor.Username,
                                  movement.TerminalId, Reference = (string?)null })
                .Skip((pageNumber - 1) * take).Take(take).ToListAsync(ct);
            return Results.Ok(rows);
        });
        app.MapGet("/api/v1/reports/views", async (HttpContext context, PosDbContext db,
            AuthenticationService auth, string reportId, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ViewReports, ct);
            if (access is null) return PermissionFailure(context);
            if (!ReportViewPolicy.IsValidReportId(reportId)) return Results.BadRequest(new { error = "invalid_report_id" });
            var rows = await db.OperatorReportViews.AsNoTracking()
                .Where(item => item.OperatorId == access.OperatorId && item.ReportId == reportId)
                .OrderBy(item => item.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(ToSavedReportView));
        });
        app.MapPost("/api/v1/reports/views", async (HttpContext context, PosDbContext db,
            AuthenticationService auth, SaveReportViewRequest request, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ViewReports, ct);
            if (access is null) return PermissionFailure(context);
            var validation = ReportViewPolicy.Validate(request.ReportId, request.View);
            if (validation is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["view"] = [validation] });
            var normalizedName = request.View.Name.Trim();
            var row = await db.OperatorReportViews.SingleOrDefaultAsync(item => item.OperatorId == access.OperatorId &&
                item.ReportId == request.ReportId && item.Name == normalizedName, ct);
            if (row is null)
            {
                row = new OperatorReportViewRecord { Id = Guid.NewGuid(), OperatorId = access.OperatorId,
                    ReportId = request.ReportId, Name = normalizedName };
                db.OperatorReportViews.Add(row);
            }
            row.ColumnsJson = JsonSerializer.Serialize(request.View.Columns);
            row.FiltersJson = JsonSerializer.Serialize(request.View.Filters);
            row.SheetJson = request.View.Sheet is null ? null : JsonSerializer.Serialize(request.View.Sheet);
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToSavedReportView(row));
        });
        app.MapDelete("/api/v1/reports/views/{viewId:guid}", async (Guid viewId, HttpContext context,
            PosDbContext db, AuthenticationService auth, CancellationToken ct) =>
        {
            var access = await AuthorizedAsync(context, auth, Permission.ViewReports, ct);
            if (access is null) return PermissionFailure(context);
            var row = await db.OperatorReportViews.SingleOrDefaultAsync(item => item.Id == viewId && item.OperatorId == access.OperatorId, ct);
            if (row is null) return Results.NotFound();
            db.OperatorReportViews.Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/audit", async (HttpContext context, PosDbContext db, AuthenticationService auth, int? limit, CancellationToken ct) =>
        {
            var access = await GetAccessAsync(context, auth, ct);
            if (access is null) return Results.Unauthorized();
            if (await AuthorizedAsync(context, auth, Permission.ViewSensitiveReports, ct) is null) return PermissionFailure(context);
            var take = Math.Clamp(limit ?? 100, 1, 500);
            return Results.Ok(await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAtUtc).Take(take)
                .Select(x => new { x.Id, x.ActorId, x.TerminalId, x.Action, x.Payload, x.OccurredAtUtc }).ToListAsync(ct));
        });
    }

    private static async Task<AccessSessionRecord?> AuthorizedAsync(HttpContext context, AuthenticationService auth, Permission permission, CancellationToken ct)
    {
        var access = await GetAccessAsync(context, auth, ct);
        if (access is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return null; }
        var mode = await auth.GetPermissionAccessModeAsync(access.OperatorId, permission, ct);
        if (mode == PermissionAccessMode.Deny) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return null; }
        if (mode == PermissionAccessMode.RequireAdminPin)
        {
            var operation = context.Request.Method + " " + context.Request.Path + context.Request.QueryString;
            var approvals = context.RequestServices.GetRequiredService<SupervisorAuthorizationStore>();
            var approvalToken = context.Request.Headers["X-Admin-Approval"].ToString();
            var fingerprint = context.Items["permission.request_fingerprint"] as string ?? string.Empty;
            if (!approvals.TryConsume(approvalToken, access.OperatorId, access.TerminalId, permission, operation, fingerprint))
            {
                context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
                context.Items["permission.challenge"] = new AdminPinChallenge("admin_pin_required", permission.ToString(), Guid.NewGuid(), operation, fingerprint);
                return null;
            }
        }
        return access;
    }

    private static IResult PermissionFailure(HttpContext context)
        => context.Items["permission.challenge"] is AdminPinChallenge challenge
            ? Results.Json(challenge, statusCode: StatusCodes.Status428PreconditionRequired)
            : Results.StatusCode(context.Response.StatusCode == StatusCodes.Status200OK ? StatusCodes.Status403Forbidden : context.Response.StatusCode);

    private static async Task<AccessSessionRecord?> GetAccessAsync(HttpContext context, AuthenticationService auth, CancellationToken ct)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length <= 7) return null;
        return await auth.FindValidSessionAsync(authorization[7..].Trim(), ct);
    }

    private static async Task<decimal?> CurrentUsdToVesRateAsync(PosDbContext db, DateTime atUtc, CancellationToken ct)
    {
        var rate = await db.ExchangeRates.AsNoTracking()
            .Where(x => ((x.FromCurrency == "USD" && x.ToCurrency == "VES") || (x.FromCurrency == "VES" && x.ToCurrency == "USD")) && x.EffectiveAtUtc <= atUtc)
            .OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1)
            .Select(x => new { x.FromCurrency, x.Rate }).FirstOrDefaultAsync(ct);
        if (rate is null || rate.Rate <= 0m) return null;
        return rate.FromCurrency == "USD" ? rate.Rate : 1m / rate.Rate;
    }

    private static string? ParseCurrency(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return Enum.TryParse<Currency>(text, out var currency) && Enum.IsDefined(currency) ? currency.ToString() : null;
    }

    private static string? ParsePriceTier(string? raw) => raw switch
    {
        "Price1" => "Price1",
        "Price2" => "Price2",
        "Price3" => "Price3",
        _ => null
    };

    private static SavedReportViewInfo ToSavedReportView(OperatorReportViewRecord row)
        => new SavedReportViewInfo(row.Id, row.ReportId, row.Name,
            JsonSerializer.Deserialize<IReadOnlyList<ReportColumnLayout>>(row.ColumnsJson) ?? [],
            JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(row.FiltersJson) ?? new Dictionary<string, string>(),
            row.UpdatedAtUtc)
        {
            Sheet = row.SheetJson is null ? null : JsonSerializer.Deserialize<ReportSheetTemplate>(row.SheetJson)
        };

    private static string ProductImageRoot()
    {
        var configured = Environment.GetEnvironmentVariable("POS_PRODUCT_IMAGE_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POS") : configured;
    }

    private static AuditRecord Audit(Guid actorId, Guid terminalId, string action, object payload, DateTime occurredAtUtc)
        => new() { Id = Guid.NewGuid(), ActorId = actorId, TerminalId = terminalId, Action = action, Payload = JsonSerializer.Serialize(payload), OccurredAtUtc = occurredAtUtc };
}

public sealed record CreateProductRequest(string? Sku, string? Name, decimal UnitPrice, string? Currency, decimal InitialStock,
    decimal? Price2 = null, decimal? Price3 = null, string? TaxTreatment = null);
public sealed record UpdateProductRequest(string? Sku, string? Name);
public sealed record ChangePriceRequest(decimal NewPrice, string? Reason, string? PriceTier = "Price1",
    decimal? ExpectedCurrentPrice = null, decimal? ExpectedUnitCost = null, bool ValidateSnapshot = false);
public sealed record SetInitialInventoryCostRequest(decimal UnitCost, string? Reason);
public sealed record ChangeProductTaxTreatmentRequest(string? TaxTreatment);
public sealed record SetExchangeRateRequest(string? FromCurrency, string? ToCurrency, decimal Rate, DateTimeOffset EffectiveAtUtc, string? Source);
public sealed record CreateCustomerRequest(string? Name, string? DocumentNumber, string? Phone, string? Currency, decimal CreditLimit);
public sealed record UpdateCustomerRequest(string? Name, string? DocumentNumber, string? Phone, decimal CreditLimit);
public sealed record ReceivablePaymentRequest(decimal Amount, string? Currency, string? Reference, string? Reason = null);
public sealed record RecordReceivableOpeningBalanceRequest(decimal AmountUsd, string? Reason, string? IdempotencyKey);
public sealed record SetActiveRequest(bool Active);
public sealed record ChangeOperatorRoleRequest(string? Role);
public sealed record OperatorPermissionModeRequest(string Permission, string Mode);
public sealed record SetOperatorPermissionsRequest(IReadOnlyList<OperatorPermissionModeRequest>? Permissions);
