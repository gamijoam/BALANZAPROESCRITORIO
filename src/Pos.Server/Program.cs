using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sales;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Finance;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Contains("--migrate", StringComparer.Ordinal);
var connectionString = builder.Configuration.GetConnectionString("Pos");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Missing ConnectionStrings:Pos. Configure it with .NET User Secrets for local development.");
builder.Services.AddDbContext<PosDbContext>(options => options.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName)));
builder.Services.AddScoped<AuthenticationService>();
builder.Services.AddScoped<SalesService>();
var app = builder.Build();
if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("Migraciones de PostgreSQL aplicadas.");
    return;
}

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (PosDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready", database = "postgresql" })
        : Results.Problem("Database is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapPost("/api/v1/setup/first-operator", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    CreateOperatorRequest request, CancellationToken cancellationToken) =>
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (!app.Environment.IsDevelopment() || remoteIp is null || !System.Net.IPAddress.IsLoopback(remoteIp))
        return Results.NotFound();
    if (await db.OperatorUsers.AnyAsync(cancellationToken)) return Results.Conflict(new { error = "Initial setup is already complete." });
    try
    {
        var operatorId = await auth.CreateOperatorAsync(request.Username, request.Pin, cancellationToken);
        return Results.Created("/api/v1/operators/" + operatorId, new { operatorId });
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = [exception.Message] });
    }
});
app.MapPost("/api/v1/auth/login", async (AuthenticationService auth, LoginRequest request, CancellationToken cancellationToken) =>
{
    if (request.TerminalId == Guid.Empty) return Results.BadRequest(new { error = "TerminalId is required." });
    AuthenticationResult? result;
    try { result = await auth.AuthenticateAsync(request.Username, request.Pin, request.TerminalId, cancellationToken); }
    catch (ArgumentException) { return Results.Unauthorized(); }
    return result is null
        ? Results.Unauthorized()
        : Results.Ok(new { accessToken = result.Token, tokenType = "Bearer", expiresAtUtc = result.ExpiresAtUtc, operatorId = result.OperatorId, terminalId = result.TerminalId });
});
app.MapGet("/api/v1/auth/session", async (HttpContext context, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var session = await auth.FindValidSessionAsync(token, cancellationToken);
    var role = session is null ? null : await auth.GetRoleAsync(session.OperatorId, cancellationToken);
    return session is null ? Results.Unauthorized() : Results.Ok(new { operatorId = session.OperatorId, terminalId = session.TerminalId, expiresAtUtc = session.ExpiresAtUtc, role = role?.ToString() });
});
app.MapPost("/api/v1/auth/logout", async (HttpContext context, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    await auth.RevokeSessionAsync(token, cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/v1/operators", async (HttpContext context, PosDbContext db, AuthenticationService auth, OperatorCreateRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.ManageUsers, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Enum.TryParse<OperatorRole>(request.Role, ignoreCase: false, out var role) || !Enum.IsDefined(role))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Role must be Cashier, Supervisor or Administrator."] });
    Guid operatorId;
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    try { operatorId = await auth.CreateOperatorAsync(request.Username, request.Pin, cancellationToken, role); }
    catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["operator"] = [exception.Message] }); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { return Results.Conflict(new { error = "username_already_exists" }); }
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "operator.created", Payload = JsonSerializer.Serialize(new { operatorId, role }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/v1/operators/{operatorId}", new { operatorId, role = role.ToString() });
});
app.MapGet("/api/v1/operators", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.ManageUsers, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await db.OperatorUsers.AsNoTracking().OrderBy(x => x.Username)
        .Select(x => new { x.Id, x.Username, x.Role, x.Active, x.CreatedAtUtc }).ToListAsync(cancellationToken));
});
app.MapPost("/api/v1/cash-sessions", async (HttpContext context, PosDbContext db, AuthenticationService auth,
    OpenCashSessionRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.CloseCashSession, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
    var openingUsd = request.OpeningBalanceUsd ?? (currency == "USD" ? request.OpeningBalance ?? 0m : 0m);
    var openingVes = request.OpeningBalanceVes ?? (currency == "VES" ? request.OpeningBalance ?? 0m : 0m);
    if (openingUsd < 0 || openingVes < 0 || currency is not ("USD" or "VES"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["openingBalance"] = ["Opening balance must be nonnegative and currency must be USD or VES."] });
    if (await db.CashSessions.AnyAsync(x => x.TerminalId == access.TerminalId && x.Status == "Open", cancellationToken))
        return Results.Conflict(new { error = "An open cash session already exists for this terminal." });
    var legacyOpening = currency == "USD" ? openingUsd : openingVes;
    var cashSession = new CashSessionRecord { Id = Guid.NewGuid(), OperatorId = access.OperatorId, TerminalId = access.TerminalId, OpeningBalance = legacyOpening, OpeningBalanceUsd = openingUsd, OpeningBalanceVes = openingVes, Currency = currency, OpenedAtUtc = DateTime.UtcNow };
    db.CashSessions.Add(cashSession);
    try { await db.SaveChangesAsync(cancellationToken); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { return Results.Conflict(new { error = "An open cash session already exists for this terminal." }); }
    return Results.Created("/api/v1/cash-sessions/" + cashSession.Id, new { cashSession.Id, cashSession.Currency, cashSession.OpeningBalance, cashSession.OpeningBalanceUsd, cashSession.OpeningBalanceVes, cashSession.OpenedAtUtc });
});
app.MapGet("/api/v1/cash-sessions/current", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    var cashSession = await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == access.TerminalId && x.OperatorId == access.OperatorId && x.Status == "Open", cancellationToken);
    return cashSession is null ? Results.NotFound() : Results.Ok(new { cashSession.Id, cashSession.Currency, cashSession.OpeningBalance, cashSession.OpeningBalanceUsd, cashSession.OpeningBalanceVes, cashSession.OpenedAtUtc });
});
app.MapPost("/api/v1/cash-sessions/{cashSessionId:guid}/close", async (Guid cashSessionId, HttpContext context, PosDbContext db, AuthenticationService auth, CloseCashSessionRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.CloseCashSession, cancellationToken)) return Results.Forbid();
    if ((request.CountedClosingUsd ?? request.CountedClosingBalance ?? 0m) < 0m || (request.CountedClosingVes ?? 0m) < 0m) return Results.ValidationProblem(new Dictionary<string, string[]> { ["countedClosingBalance"] = ["Los saldos contados no pueden ser negativos."] });
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"OperatorId\" = {access.OperatorId} AND \"TerminalId\" = {access.TerminalId} AND \"Status\" = 'Open' FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);
    if (cashSession is null) return Results.NotFound();
    var receipts = await (from payment in db.Payments
                          join sale in db.Sales on payment.SaleId equals sale.Id
                          where sale.CashSessionId == cashSessionId &&
                                (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                          select new { payment.Amount, payment.Currency }).ToListAsync(cancellationToken);
    var movements = await db.CashMovements.Where(x => x.CashSessionId == cashSessionId).ToListAsync(cancellationToken);
    var countedUsd = request.CountedClosingUsd ?? (cashSession.Currency == "USD" ? request.CountedClosingBalance ?? 0m : 0m);
    var countedVes = request.CountedClosingVes ?? (cashSession.Currency == "VES" ? request.CountedClosingBalance ?? 0m : 0m);
    var openingUsd = cashSession.OpeningBalanceUsd == 0m && cashSession.Currency == "USD" ? cashSession.OpeningBalance : cashSession.OpeningBalanceUsd;
    var openingVes = cashSession.OpeningBalanceVes == 0m && cashSession.Currency == "VES" ? cashSession.OpeningBalance : cashSession.OpeningBalanceVes;
    var ledger = CashSessionLedger.Calculate(openingUsd, openingVes,
        receipts.Select(x => new CashReceipt(x.Currency, x.Amount)),
        movements.Select(x => new CashMovement(x.Currency, x.Direction, x.Amount)), countedUsd, countedVes);
    var usd = ledger.Usd;
    var ves = ledger.Ves;
    var reconciliation = cashSession.Currency == "USD" ? usd : ves;
    cashSession.Status = "Closed";
    cashSession.ClosedAtUtc = DateTime.UtcNow;
    cashSession.ExpectedClosingBalance = reconciliation.ExpectedBalance;
    cashSession.CountedClosingBalance = reconciliation.CountedBalance;
    cashSession.Variance = reconciliation.Variance;
    cashSession.ExpectedClosingBalanceUsd = usd.ExpectedBalance; cashSession.CountedClosingBalanceUsd = usd.CountedBalance; cashSession.VarianceUsd = usd.Variance;
    cashSession.ExpectedClosingBalanceVes = ves.ExpectedBalance; cashSession.CountedClosingBalanceVes = ves.CountedBalance; cashSession.VarianceVes = ves.Variance;
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "cash-session.closed", Payload = JsonSerializer.Serialize(new { cashSessionId, usd, ves }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new { cashSessionId, reconciliation.ExpectedBalance, reconciliation.CountedBalance, reconciliation.Variance,
        expectedUsd = usd.ExpectedBalance, countedUsd = usd.CountedBalance, varianceUsd = usd.Variance,
        expectedVes = ves.ExpectedBalance, countedVes = ves.CountedBalance, varianceVes = ves.Variance });
});
app.MapPost("/api/v1/cash-sessions/{cashSessionId:guid}/movements", async (Guid cashSessionId, HttpContext context, PosDbContext db, AuthenticationService auth, CashMovementRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.CloseCashSession, cancellationToken)) return Results.Forbid();
    var direction = (request.Direction ?? string.Empty).Trim();
    var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
    var reason = (request.Reason ?? string.Empty).Trim();
    if (request.Amount <= 0m || direction is not ("In" or "Out") || (request.Currency is not null && currency is not ("USD" or "VES")) || reason.Length is < 3 or > 250)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["movement"] = ["A positive amount, direction In/Out and reason (3–250 characters) are required."] });
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"OperatorId\" = {access.OperatorId} AND \"TerminalId\" = {access.TerminalId} AND \"Status\" = 'Open' FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
    if (cashSession is null) return Results.NotFound();
    if (request.Currency is null) currency = cashSession.Currency;
    var cashReceipts = await (from payment in db.Payments join sale in db.Sales on payment.SaleId equals sale.Id
                              where sale.CashSessionId == cashSessionId && (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                              select new { payment.Amount, payment.Currency }).ToListAsync(cancellationToken);
    var priorMovements = await db.CashMovements.Where(x => x.CashSessionId == cashSessionId).ToListAsync(cancellationToken);
    var opening = currency == "USD"
        ? cashSession.OpeningBalanceUsd == 0m && cashSession.Currency == "USD" ? cashSession.OpeningBalance : cashSession.OpeningBalanceUsd
        : cashSession.OpeningBalanceVes == 0m && cashSession.Currency == "VES" ? cashSession.OpeningBalance : cashSession.OpeningBalanceVes;
    var expectedCash = CashSessionLedger.ExpectedBalance(opening,
        cashReceipts.Select(x => new CashReceipt(x.Currency, x.Amount)),
        priorMovements.Select(x => new CashMovement(x.Currency, x.Direction, x.Amount)), currency);
    if (direction == "Out" && request.Amount > expectedCash) return Results.Conflict(new { error = "insufficient_cash" });
    var movement = new CashMovementRecord { Id = Guid.NewGuid(), CashSessionId = cashSessionId, Amount = request.Amount,
        Direction = direction, Currency = currency, Reason = reason, ActorId = access.OperatorId, OccurredAtUtc = DateTime.UtcNow };
    db.CashMovements.Add(movement);
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = $"cash-session.{direction.ToLowerInvariant()}", Payload = JsonSerializer.Serialize(new { cashSessionId, movement.Amount, movement.Reason }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/v1/cash-sessions/{cashSessionId}/movements/{movement.Id}", new { movement.Id, movement.Amount, movement.Direction, movement.Reason, movement.OccurredAtUtc });
});
app.MapPost("/api/v1/sales", async (HttpContext context, AuthenticationService auth, SalesService sales, ConfirmSaleRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (request.Checkout is null) return Results.BadRequest(new { error = "Checkout is required." });
    if (request.Checkout.Payments.Any(x => string.Equals(x.Method, "credit", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Method, "fiado", StringComparison.OrdinalIgnoreCase)) &&
        !await auth.HasPermissionAsync(access.OperatorId, Permission.ManageReceivables, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    try
    {
        var result = await sales.ConfirmAsync(access.OperatorId, access.TerminalId, request.CashSessionId, key, request.Checkout, cancellationToken);
        if (result.Error == "idempotency_conflict") return Results.Conflict(new { error = result.Error });
        return result.Replayed
            ? Results.Ok(new { saleId = result.SaleId, replayed = true })
            : Results.Created("/api/v1/sales/" + result.SaleId, new { saleId = result.SaleId, replayed = false });
    }
    catch (CheckoutRejectedException exception)
    {
        return exception.Code == "insufficient_stock"
            ? Results.Conflict(new { error = exception.Code })
            : Results.UnprocessableEntity(new { error = exception.Code });
    }
});
app.MapGet("/api/v1/products", async (HttpContext context, PosDbContext db, AuthenticationService auth, string? query, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null || await auth.FindValidSessionAsync(token, cancellationToken) is null) return Results.Unauthorized();
    var search = (query ?? string.Empty).Trim();
    if (search.Length > 100) return Results.BadRequest(new { error = "Search text is too long." });
    var products = await db.Products.AsNoTracking()
        .Where(x => x.Active && (search == "" || EF.Functions.ILike(x.Sku, search + "%") || EF.Functions.ILike(x.Name, "%" + search + "%")))
        .OrderBy(x => x.Name).Take(30)
        .Select(x => new { x.Id, x.Sku, x.Name, x.UnitPrice, x.Currency, hasImage = x.ImageFileName != null })
        .ToListAsync(cancellationToken);
    return Results.Ok(products);
});
app.MapGet("/api/v1/inventory", async (HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.AdjustStock, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var inventory = await (from product in db.Products.AsNoTracking().Where(x => x.Active)
                           join stock in db.Stocks.AsNoTracking() on product.Id equals stock.ProductId into stockRows
                           from stock in stockRows.DefaultIfEmpty()
                           orderby product.Name
                           select new { product.Id, product.Sku, product.Name, product.UnitPrice, product.Currency, Quantity = stock == null ? 0m : stock.Quantity, hasImage = product.ImageFileName != null })
        .ToListAsync(cancellationToken);
    return Results.Ok(inventory);
});
app.MapPost("/api/v1/inventory/{productId:guid}/adjustments", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, InventoryAdjustmentRequest request, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.AdjustStock, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.QuantityDelta == 0m || decimal.Round(request.QuantityDelta, 3) != request.QuantityDelta ||
        string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 250)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["adjustment"] = ["A nonzero quantity with at most three decimals and a reason up to 250 characters is required."] });
    if (!await db.Products.AnyAsync(x => x.Id == productId && x.Active, cancellationToken)) return Results.NotFound();
    if (!await db.Stocks.AnyAsync(x => x.ProductId == productId, cancellationToken)) return Results.Conflict(new { error = "stock_record_missing" });
    StockMovement movement;
    try { movement = StockMovement.Create(productId, request.QuantityDelta, request.Reason, access.OperatorId, DateTimeOffset.UtcNow); }
    catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["adjustment"] = [exception.Message] }); }
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stocks SET \"Quantity\" = \"Quantity\" + {movement.QuantityDelta}, \"Version\" = \"Version\" + 1 WHERE \"ProductId\" = {productId} AND \"Quantity\" + {movement.QuantityDelta} >= 0", cancellationToken);
    if (affected != 1) return Results.Conflict(new { error = "insufficient_stock" });
    var record = new StockMovementRecord { Id = Guid.NewGuid(), ProductId = movement.ProductId, QuantityDelta = movement.QuantityDelta,
        Reason = movement.Reason, ActorId = movement.ActorId, TerminalId = access.TerminalId, OccurredAtUtc = movement.OccurredAtUtc.UtcDateTime };
    db.StockMovements.Add(record);
    db.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = access.OperatorId, TerminalId = access.TerminalId,
        Action = "inventory.adjusted", Payload = JsonSerializer.Serialize(new { record.ProductId, record.QuantityDelta, record.Reason }), OccurredAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    var quantity = await db.Stocks.AsNoTracking().Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync(cancellationToken);
    return Results.Created($"/api/v1/inventory/{productId}/movements/{record.Id}", new { record.Id, record.ProductId, record.QuantityDelta, quantity, record.Reason, record.OccurredAtUtc });
});
app.MapGet("/api/v1/inventory/{productId:guid}/movements", async (Guid productId, HttpContext context, PosDbContext db, AuthenticationService auth, CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var access = await auth.FindValidSessionAsync(token, cancellationToken);
    if (access is null) return Results.Unauthorized();
    if (!await auth.HasPermissionAsync(access.OperatorId, Permission.AdjustStock, cancellationToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var movements = await db.StockMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderByDescending(x => x.OccurredAtUtc).Take(100)
        .Select(x => new { x.Id, x.QuantityDelta, x.Reason, x.ActorId, x.OccurredAtUtc }).ToListAsync(cancellationToken);
    return Results.Ok(movements);
});
app.MapHito4();
app.Run();

static string? ReadBearerToken(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Length > 7
        ? authorization[7..].Trim()
        : null;
}

public sealed record CreateOperatorRequest(string? Username, string? Pin);
public sealed record OperatorCreateRequest(string? Username, string? Pin, string? Role);
public sealed record LoginRequest(string? Username, string? Pin, Guid TerminalId);
public sealed record OpenCashSessionRequest(decimal? OpeningBalance = null, string? Currency = null, decimal? OpeningBalanceUsd = null, decimal? OpeningBalanceVes = null);
public sealed record CloseCashSessionRequest(decimal? CountedClosingBalance = null, decimal? CountedClosingUsd = null, decimal? CountedClosingVes = null);
public sealed record CashMovementRequest(decimal Amount, string? Direction, string? Reason, string? Currency = null);
public sealed record InventoryAdjustmentRequest(decimal QuantityDelta, string? Reason);
public sealed record ConfirmSaleRequest(Guid CashSessionId, CheckoutRequest? Checkout);
