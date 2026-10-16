using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var options = LicenseServiceOptions.Load(builder.Configuration);
var adminWebAuth = AdminWebAuthentication.Load(builder.Configuration);
var issuer = new PosLicenseIssuer(options.KeyId, options.PrivateKeyPath);
var store = new LicenseSubscriptionStore(options.StorePath);
var audit = new LicenseAuditStore(options.AuditPath);
builder.Services.AddSingleton(issuer);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(audit);
builder.Services.AddSingleton(adminWebAuth);
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        if (context.Request.ContentLength is > 256 * 1024)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    }
    await next();
});

var adminWebRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var adminPagePath = Path.Combine(adminWebRoot, "license-admin.html");
var adminScriptPath = Path.Combine(adminWebRoot, "license-admin.js");
var adminStylePath = Path.Combine(adminWebRoot, "license-admin.css");
if (adminWebAuth.Enabled && new[] { adminPagePath, adminScriptPath, adminStylePath }.Any(path => !File.Exists(path)))
    throw new FileNotFoundException("No están instalados los archivos del panel administrativo de licencias.");
app.MapGet("/admin", (AdminWebAuthentication auth) => auth.Enabled
    ? (IResult)Results.File(adminPagePath, "text/html; charset=utf-8")
    : Results.NotFound());
app.MapGet("/admin/license-admin.js", (AdminWebAuthentication auth) => auth.Enabled
    ? (IResult)Results.File(adminScriptPath, "text/javascript; charset=utf-8")
    : Results.NotFound());
app.MapGet("/admin/license-admin.css", (AdminWebAuthentication auth) => auth.Enabled
    ? (IResult)Results.File(adminStylePath, "text/css; charset=utf-8")
    : Results.NotFound());

app.MapPost("/admin/api/session", (AdminWebLoginRequest request, HttpContext context, AdminWebAuthentication auth) =>
{
    if (!auth.Enabled) return Results.NotFound();
    var forwardedAddress = context.Request.Headers["X-Forwarded-For"].ToString().Split(',').LastOrDefault()?.Trim();
    var remoteAddress = System.Net.IPAddress.TryParse(forwardedAddress, out var forwardedIp)
        ? forwardedIp.ToString()
        : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!auth.TryLogin(request.Username, request.Password, request.TotpCode, remoteAddress,
            DateTimeOffset.UtcNow, out var session, out var rateLimited))
        return rateLimited
            ? Results.Json(new { error = "login_rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests)
            : Results.Json(new { error = "credentials_invalid" }, statusCode: StatusCodes.Status401Unauthorized);
    auth.SetSessionCookie(context, session!);
    return Results.Ok(new { csrfToken = session!.CsrfToken, expiresAtUtc = session.ExpiresAtUtc });
});
app.MapGet("/admin/api/session", (HttpContext context, AdminWebAuthentication auth) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: false, out var session)) return Results.Unauthorized();
    return Results.Ok(new { username = session!.Username, csrfToken = session.CsrfToken, expiresAtUtc = session.ExpiresAtUtc });
});
app.MapDelete("/admin/api/session", (HttpContext context, AdminWebAuthentication auth) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    auth.ClearSessionCookie(context);
    return Results.NoContent();
});
app.MapGet("/admin/api/licenses", async (HttpContext context, AdminWebAuthentication auth,
    LicenseSubscriptionStore subscriptions, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: false, out _)) return Results.Unauthorized();
    var records = await subscriptions.ListAsync(cancellationToken);
    return Results.Ok(records.Select(item => new
    {
        item.LicenseId, item.InstallationId, item.CustomerRif, item.CustomerName, item.Plan, item.MaxTerminals,
        item.Entitlements, item.PaidThroughUtc, item.Active, item.ControlVersion, item.SuspensionReason,
        item.LastSeenAtUtc, payments = item.Payments ?? []
    }));
});
app.MapGet("/admin/api/audit", async (HttpContext context, AdminWebAuthentication auth,
    LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: false, out _)) return Results.Unauthorized();
    return Results.Ok(await auditLog.ReadRecentAsync(30, cancellationToken));
});
app.MapPost("/admin/api/licenses", async (CreateSubscriptionRequest request, HttpContext context,
    AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog,
    PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var renewalToken = LicenseSecrets.CreateToken();
    var nowUtc = DateTimeOffset.UtcNow;
    var payments = request.InitialPaymentAmount is { } initialAmount
        ? new[] { new LicensePayment(Guid.NewGuid(), nowUtc, initialAmount, request.InitialPaymentCurrency!.Trim().ToUpperInvariant(),
            request.InitialPaymentReference!.Trim(), request.PaidThroughUtc) }
        : Array.Empty<LicensePayment>();
    var subscription = new LicenseSubscription("lic_" + Guid.NewGuid().ToString("N"), request.InstallationId,
        request.CustomerRif.Trim(), request.CustomerName.Trim(), request.Plan.Trim(), request.MaxTerminals,
        request.Entitlements.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), request.PaidThroughUtc,
        LicenseSecrets.HashToken(renewalToken), licenseIssuer.EncryptRenewalToken(renewalToken), Active: true, Payments: payments);
    try { await subscriptions.AddAsync(subscription, cancellationToken); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    await auditLog.AppendAsync("subscription.created", subscription.LicenseId, subscription.InstallationId,
        new { plan = subscription.Plan, expiresAtUtc = subscription.PaidThroughUtc, paymentAmount = request.InitialPaymentAmount,
            paymentCurrency = request.InitialPaymentCurrency, paymentReference = request.InitialPaymentReference }, cancellationToken);
    var document = licenseIssuer.Issue(subscription.ToLicense(nowUtc, renewalToken));
    return Results.Ok(new { licenseId = subscription.LicenseId, expiresAtUtc = subscription.PaidThroughUtc,
        documentBase64 = Convert.ToBase64String(document) });
});
app.MapGet("/admin/api/licenses/{licenseId}/document", async (string licenseId, HttpContext context,
    AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog,
    PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: false, out _)) return Results.Unauthorized();
    var subscription = await subscriptions.FindAsync(licenseId, cancellationToken);
    if (subscription is null) return Results.NotFound();
    var token = licenseIssuer.DecryptRenewalToken(subscription.RenewalTokenEncrypted);
    var document = licenseIssuer.Issue(subscription.ToLicense(DateTimeOffset.UtcNow, token));
    await auditLog.AppendAsync("license.document-issued", subscription.LicenseId, subscription.InstallationId,
        new { expiresAtUtc = subscription.PaidThroughUtc }, cancellationToken);
    return Results.Ok(new { licenseId, documentBase64 = Convert.ToBase64String(document) });
});
app.MapPost("/admin/api/licenses/{licenseId}/payments", async (string licenseId, RecordPaymentRequest request,
    HttpContext context, AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions,
    LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var current = await subscriptions.FindAsync(licenseId, cancellationToken);
    if (current is null) return Results.NotFound();
    if (request.PaidThroughUtc <= current.PaidThroughUtc)
        return Results.BadRequest(new { error = "La nueva fecha debe ser posterior al vencimiento registrado." });
    var payment = new LicensePayment(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Amount,
        request.Currency.Trim().ToUpperInvariant(), request.Reference.Trim(), request.PaidThroughUtc);
    var updated = await subscriptions.RecordPaymentAsync(licenseId, payment, cancellationToken);
    if (updated is null) return Results.NotFound();
    await auditLog.AppendAsync("payment.recorded", updated.LicenseId, updated.InstallationId,
        new { payment.PaymentId, payment.Amount, payment.Currency, payment.Reference, payment.PaidThroughUtc }, cancellationToken);
    return Results.Ok(new { licenseId, payment, active = updated.Active });
});
app.MapPut("/admin/api/licenses/{licenseId}/terms", async (string licenseId, UpdateLicenseTermsRequest request,
    HttpContext context, AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions,
    LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var updated = await subscriptions.UpdateTermsAsync(licenseId, request, cancellationToken);
    if (updated is null) return Results.NotFound();
    await auditLog.AppendAsync("subscription.terms-updated", updated.LicenseId, updated.InstallationId,
        new { updated.Plan, updated.MaxTerminals, updated.Entitlements, updated.ControlVersion }, cancellationToken);
    return Results.Ok(new { updated.LicenseId, updated.Plan, updated.MaxTerminals, updated.Entitlements });
});
app.MapPost("/admin/api/licenses/{licenseId}/suspend", async (string licenseId, SuspendSubscriptionRequest request,
    HttpContext context, AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions,
    LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 512)
        return Results.BadRequest(new { error = "reason es obligatorio y admite hasta 512 caracteres." });
    var updated = await subscriptions.SetActiveAsync(licenseId, active: false, request.Reason.Trim(), cancellationToken);
    if (updated is null) return Results.NotFound();
    await auditLog.AppendAsync("subscription.suspended", updated.LicenseId, updated.InstallationId,
        new { reason = updated.SuspensionReason, controlVersion = updated.ControlVersion }, cancellationToken);
    return Results.Ok(new { licenseId, active = false });
});
app.MapPost("/admin/api/licenses/{licenseId}/enable", async (string licenseId, HttpContext context,
    AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog,
    CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    var current = await subscriptions.FindAsync(licenseId, cancellationToken);
    if (current is null) return Results.NotFound();
    if (current.PaidThroughUtc <= DateTimeOffset.UtcNow)
        return Results.Conflict(new { error = "Registra primero el pago y una fecha vigente antes de reactivar esta licencia." });
    var updated = await subscriptions.SetActiveAsync(licenseId, active: true, null, cancellationToken);
    await auditLog.AppendAsync("subscription.reactivated", updated!.LicenseId, updated.InstallationId,
        new { controlVersion = updated.ControlVersion }, cancellationToken);
    return Results.Ok(new { licenseId, active = true });
});
app.MapPost("/admin/api/recoveries", async (IssueRecoveryRequest request, HttpContext context,
    AdminWebAuthentication auth, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog,
    PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (!auth.Enabled) return Results.NotFound();
    if (!auth.IsAuthorized(context, requireCsrf: true, out _)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var source = request.Request;
    var subscription = await subscriptions.FindAsync(source.LicenseId, cancellationToken);
    if (subscription is null || subscription.InstallationId != source.InstallationId)
        return Results.NotFound(new { error = "No existe una suscripción para esta solicitud de instalación." });
    var nowUtc = DateTimeOffset.UtcNow;
    var grant = new PosLicenseRecoveryGrantDocument(1, source.LicenseId, source.InstallationId, source.RequestId,
        source.Nonce, nowUtc, nowUtc.AddHours(request.DurationHours), request.Reason.Trim());
    var signedDocument = licenseIssuer.IssueRecovery(grant);
    var saved = await subscriptions.GetOrCreateRecoveryGrantAsync(subscription.LicenseId, source,
        signedDocument, grant.ExpiresAtUtc, cancellationToken);
    if (saved.Issuance is null)
        return Results.Conflict(new { error = "La solicitud ya se utilizó con otros datos o se alcanzó el límite de desbloqueos." });
    if (saved.Created)
        await auditLog.AppendAsync("offline-recovery.issued", subscription.LicenseId, subscription.InstallationId,
            new { requestId = source.RequestId, expiresAtUtc = saved.Issuance.ExpiresAtUtc,
                durationHours = request.DurationHours, reason = grant.Reason }, cancellationToken);
    else
        await auditLog.AppendAsync("offline-recovery.retrieved", subscription.LicenseId, subscription.InstallationId,
            new { requestId = source.RequestId, expiresAtUtc = saved.Issuance.ExpiresAtUtc }, cancellationToken);
    return Results.Ok(new { licenseId = subscription.LicenseId, installationId = subscription.InstallationId,
        source.RequestId, expiresAtUtc = saved.Issuance.ExpiresAtUtc, documentBase64 = saved.Issuance.DocumentBase64,
        alreadyIssued = !saved.Created });
});

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/v1/license/public-key", (PosLicenseIssuer licenseIssuer) =>
    Results.Ok(new { schemaVersion = 1, keys = new[] { new { keyId = licenseIssuer.KeyId, publicKeyPem = licenseIssuer.PublicKeyPem } } }));

app.MapPost("/api/v1/renewals", async (RenewalRequest request, HttpContext context,
    LicenseSubscriptionStore subscriptions, PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (request.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(request.LicenseId) ||
        string.IsNullOrWhiteSpace(request.RenewalToken) || request.RenewalToken.Length > 256)
        return Results.Unauthorized();

    var subscription = await subscriptions.FindAsync(request.LicenseId, cancellationToken);
    if (subscription is null || subscription.InstallationId != request.InstallationId ||
        !LicenseSecrets.Matches(request.RenewalToken, subscription.RenewalTokenHash))
        return Results.Unauthorized();

    var nowUtc = DateTimeOffset.UtcNow;
    subscription = await subscriptions.TouchLastSeenAsync(subscription.LicenseId, nowUtc, cancellationToken) ?? subscription;
    if (!subscription.Active)
    {
        if (request.ProtocolVersion < 2) return Results.NoContent();
        if (subscription.ControlVersion <= 0)
            subscription = await subscriptions.EnsureSuspensionVersionAsync(subscription.LicenseId, cancellationToken) ?? subscription;
        var control = new PosLicenseControlDocument(1, subscription.LicenseId, subscription.InstallationId,
            subscription.ControlVersion, "Suspended", subscription.SuspensionReason, nowUtc);
        return Results.Ok(new RenewalResponse(null, null, Convert.ToBase64String(licenseIssuer.IssueControl(control))));
    }
    if (subscription.PaidThroughUtc <= nowUtc)
        return Results.NoContent();

    var renewalToken = licenseIssuer.DecryptRenewalToken(subscription.RenewalTokenEncrypted);
    var document = licenseIssuer.Issue(subscription.ToLicense(nowUtc, renewalToken));
    return Results.Ok(new RenewalResponse(Convert.ToBase64String(document), subscription.PaidThroughUtc, null));
});

app.MapPost("/api/v1/admin/licenses", async (CreateSubscriptionRequest request, HttpContext context,
    LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();

    var validationError = request.Validate();
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    var renewalToken = LicenseSecrets.CreateToken();
    var nowUtc = DateTimeOffset.UtcNow;
    var subscription = new LicenseSubscription(
        "lic_" + Guid.NewGuid().ToString("N"), request.InstallationId, request.CustomerRif.Trim(),
        request.CustomerName.Trim(), request.Plan.Trim(), request.MaxTerminals,
        request.Entitlements.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        request.PaidThroughUtc, LicenseSecrets.HashToken(renewalToken), licenseIssuer.EncryptRenewalToken(renewalToken), Active: true,
        Payments: request.InitialPaymentAmount is { } initialAmount
            ? [new LicensePayment(Guid.NewGuid(), nowUtc, initialAmount, request.InitialPaymentCurrency!.Trim().ToUpperInvariant(),
                request.InitialPaymentReference!.Trim(), request.PaidThroughUtc)]
            : []);
    try { await subscriptions.AddAsync(subscription, cancellationToken); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    await auditLog.AppendAsync("subscription.created", subscription.LicenseId, subscription.InstallationId,
        new { plan = subscription.Plan, expiresAtUtc = subscription.PaidThroughUtc,
            initialPayment = subscription.Payments?.Select(payment => new { payment.Amount, payment.Currency, payment.Reference }) }, cancellationToken);

    var document = licenseIssuer.Issue(subscription.ToLicense(nowUtc, renewalToken));
    return Results.Created($"/api/v1/admin/licenses/{subscription.LicenseId}", new
    {
        licenseId = subscription.LicenseId,
        installationId = subscription.InstallationId,
        expiresAtUtc = subscription.PaidThroughUtc,
        documentBase64 = Convert.ToBase64String(document)
    });
});

app.MapGet("/api/v1/admin/licenses", async (HttpContext context, LicenseSubscriptionStore subscriptions,
    CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken)) return Results.Unauthorized();
    var records = await subscriptions.ListAsync(cancellationToken);
    return Results.Ok(records.Select(item => new
    {
        item.LicenseId, item.InstallationId, item.CustomerRif, item.CustomerName, item.Plan, item.MaxTerminals,
        item.Entitlements, item.PaidThroughUtc, item.Active, item.ControlVersion, item.SuspensionReason,
        item.LastSeenAtUtc, payments = item.Payments ?? []
    }));
});

app.MapGet("/api/v1/admin/installations/{installationId:guid}/license-document", async (Guid installationId,
    HttpContext context, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    var subscription = await subscriptions.FindByInstallationAsync(installationId, cancellationToken);
    if (subscription is null) return Results.NotFound();
    var token = licenseIssuer.DecryptRenewalToken(subscription.RenewalTokenEncrypted);
    var document = licenseIssuer.Issue(subscription.ToLicense(DateTimeOffset.UtcNow, token));
    await auditLog.AppendAsync("license.document-issued", subscription.LicenseId, installationId,
        new { expiresAtUtc = subscription.PaidThroughUtc }, cancellationToken);
    return Results.Ok(new { licenseId = subscription.LicenseId, installationId, expiresAtUtc = subscription.PaidThroughUtc,
        documentBase64 = Convert.ToBase64String(document) });
});

app.MapPut("/api/v1/admin/licenses/{licenseId}/paid-through", async (string licenseId, PaidThroughRequest request,
    HttpContext context, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    if (request.PaidThroughUtc <= DateTimeOffset.UtcNow || request.PaidThroughUtc > DateTimeOffset.UtcNow.AddYears(5))
        return Results.BadRequest(new { error = "paidThroughUtc debe estar en el futuro y no superar cinco años." });

    if (request.PaidThroughUtc.Offset != TimeSpan.Zero)
        return Results.BadRequest(new { error = "paidThroughUtc debe estar expresado en UTC." });
    var current = await subscriptions.FindAsync(licenseId, cancellationToken);
    if (current is null) return Results.NotFound();
    if (request.PaidThroughUtc <= current.PaidThroughUtc)
        return Results.BadRequest(new { error = "La nueva fecha debe ser posterior al vencimiento registrado." });
    var updated = await subscriptions.SetPaidThroughAsync(licenseId, request.PaidThroughUtc, cancellationToken);
    if (updated is not null)
        await auditLog.AppendAsync("subscription.expiry-updated", updated.LicenseId, updated.InstallationId,
            new { expiresAtUtc = updated.PaidThroughUtc }, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, paidThroughUtc = updated.PaidThroughUtc });
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/payments", async (string licenseId, RecordPaymentRequest request,
    HttpContext context, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var current = await subscriptions.FindAsync(licenseId, cancellationToken);
    if (current is null) return Results.NotFound();
    if (request.PaidThroughUtc <= current.PaidThroughUtc)
        return Results.BadRequest(new { error = "La nueva fecha debe ser posterior al vencimiento registrado." });
    var payment = new LicensePayment(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Amount, request.Currency.Trim().ToUpperInvariant(),
        request.Reference.Trim(), request.PaidThroughUtc);
    var updated = await subscriptions.RecordPaymentAsync(licenseId, payment, cancellationToken);
    if (updated is null) return Results.NotFound();
    await auditLog.AppendAsync("payment.recorded", updated.LicenseId, updated.InstallationId,
        new { payment.PaymentId, payment.Amount, payment.Currency, payment.Reference, payment.PaidThroughUtc }, cancellationToken);
    return Results.Ok(new { licenseId, payment, active = updated.Active, controlVersion = updated.ControlVersion });
});

app.MapPut("/api/v1/admin/licenses/{licenseId}/terms", async (string licenseId, UpdateLicenseTermsRequest request,
    HttpContext context, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var updated = await subscriptions.UpdateTermsAsync(licenseId, request, cancellationToken);
    if (updated is null) return Results.NotFound();
    await auditLog.AppendAsync("subscription.terms-updated", updated.LicenseId, updated.InstallationId,
        new { updated.Plan, updated.MaxTerminals, updated.Entitlements, updated.ControlVersion }, cancellationToken);
    return Results.Ok(new { updated.LicenseId, updated.Plan, updated.MaxTerminals, updated.Entitlements, updated.ControlVersion });
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/disable", async (string licenseId, HttpContext context,
    LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    var updated = await subscriptions.SetActiveAsync(licenseId, active: false, "Suspensión administrativa", cancellationToken);
    if (updated is not null)
        await auditLog.AppendAsync("subscription.suspended", updated.LicenseId, updated.InstallationId,
            new { reason = updated.SuspensionReason, controlVersion = updated.ControlVersion }, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, active = false });
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/enable", async (string licenseId, HttpContext context,
    LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    var current = await subscriptions.FindAsync(licenseId, cancellationToken);
    if (current is null) return Results.NotFound();
    if (current.PaidThroughUtc <= DateTimeOffset.UtcNow)
        return Results.Conflict(new { error = "Registra primero el pago y una fecha vigente antes de reactivar esta licencia." });
    var updated = await subscriptions.SetActiveAsync(licenseId, active: true, null, cancellationToken);
    if (updated is not null)
        await auditLog.AppendAsync("subscription.reactivated", updated.LicenseId, updated.InstallationId,
            new { controlVersion = updated.ControlVersion }, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, active = true });
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/suspend", async (string licenseId, SuspendSubscriptionRequest request,
    HttpContext context, LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 512)
        return Results.BadRequest(new { error = "reason es obligatorio y admite hasta 512 caracteres." });
    var updated = await subscriptions.SetActiveAsync(licenseId, active: false, request.Reason.Trim(), cancellationToken);
    if (updated is not null)
        await auditLog.AppendAsync("subscription.suspended", updated.LicenseId, updated.InstallationId,
            new { reason = updated.SuspensionReason, controlVersion = updated.ControlVersion }, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, active = false, controlVersion = updated.ControlVersion });
});

app.MapPost("/api/v1/admin/recoveries", async (IssueRecoveryRequest request, HttpContext context,
    LicenseSubscriptionStore subscriptions, LicenseAuditStore auditLog, PosLicenseIssuer licenseIssuer,
    CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken)) return Results.Unauthorized();
    var validationError = request.Validate();
    if (validationError is not null) return Results.BadRequest(new { error = validationError });
    var source = request.Request;
    var subscription = await subscriptions.FindAsync(source.LicenseId, cancellationToken);
    if (subscription is null || subscription.InstallationId != source.InstallationId)
        return Results.NotFound(new { error = "No existe una suscripción para esta solicitud de instalación." });

    var nowUtc = DateTimeOffset.UtcNow;
    var grant = new PosLicenseRecoveryGrantDocument(1, source.LicenseId, source.InstallationId, source.RequestId,
        source.Nonce, nowUtc, nowUtc.AddHours(request.DurationHours), request.Reason.Trim());
    var document = licenseIssuer.IssueRecovery(grant);
    var saved = await subscriptions.GetOrCreateRecoveryGrantAsync(subscription.LicenseId, source,
        document, grant.ExpiresAtUtc, cancellationToken);
    if (saved.Issuance is null)
        return Results.Conflict(new { error = "La solicitud ya se utilizó con otros datos o se alcanzó el límite de desbloqueos." });
    if (saved.Created)
        await auditLog.AppendAsync("offline-recovery.issued", subscription.LicenseId, subscription.InstallationId,
            new { requestId = source.RequestId, expiresAtUtc = saved.Issuance.ExpiresAtUtc,
                durationHours = request.DurationHours, reason = grant.Reason }, cancellationToken);
    else
        await auditLog.AppendAsync("offline-recovery.retrieved", subscription.LicenseId, subscription.InstallationId,
            new { requestId = source.RequestId, expiresAtUtc = saved.Issuance.ExpiresAtUtc }, cancellationToken);
    return Results.Ok(new { licenseId = subscription.LicenseId, installationId = subscription.InstallationId,
        source.RequestId, expiresAtUtc = saved.Issuance.ExpiresAtUtc, documentBase64 = saved.Issuance.DocumentBase64,
        alreadyIssued = !saved.Created });
});

app.Run();

public sealed record RenewalRequest(string LicenseId, Guid InstallationId, string RenewalToken, int ProtocolVersion = 1);
public sealed record RenewalResponse(string? DocumentBase64, DateTimeOffset? ExpiresAtUtc, string? ControlDocumentBase64);
public sealed record PaidThroughRequest(DateTimeOffset PaidThroughUtc);
public sealed record RecordPaymentRequest(decimal Amount, string Currency, string Reference, DateTimeOffset PaidThroughUtc)
{
    public string? Validate()
    {
        if (Amount <= 0m || Amount > 1_000_000_000m) return "amount debe ser mayor que cero y no superar 1.000.000.000.";
        if (string.IsNullOrWhiteSpace(Currency) || Currency.Length > 8 || !Regex.IsMatch(Currency, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
            return "currency es obligatorio y admite hasta 8 caracteres alfanuméricos.";
        if (string.IsNullOrWhiteSpace(Reference) || Reference.Length > 128) return "reference es obligatorio y admite hasta 128 caracteres.";
        if (PaidThroughUtc.Offset != TimeSpan.Zero || PaidThroughUtc <= DateTimeOffset.UtcNow || PaidThroughUtc > DateTimeOffset.UtcNow.AddYears(5))
            return "paidThroughUtc debe estar en UTC, en el futuro y no superar cinco años.";
        return null;
    }
}
public sealed record UpdateLicenseTermsRequest(string Plan, int MaxTerminals, IReadOnlyList<string> Entitlements)
{
    private static readonly Regex EntitlementPattern = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Plan) || Plan.Length > 64) return "plan es obligatorio y admite hasta 64 caracteres.";
        if (MaxTerminals is < 1 or > 1000) return "maxTerminals debe estar entre 1 y 1000.";
        if (Entitlements is null || Entitlements.Count > 256 || Entitlements.Any(item => string.IsNullOrWhiteSpace(item) || !EntitlementPattern.IsMatch(item)))
            return "entitlements debe ser una lista de hasta 256 permisos con formato válido.";
        if (Entitlements.Distinct(StringComparer.Ordinal).Count() != Entitlements.Count) return "entitlements no puede repetir valores.";
        return null;
    }
}
public sealed record SuspendSubscriptionRequest(string Reason);
public sealed record LicenseRecoveryRequestDocument(int SchemaVersion, string LicenseId, Guid InstallationId,
    Guid RequestId, string Nonce, DateTimeOffset CreatedAtUtc)
{
    public string? Validate(DateTimeOffset nowUtc)
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(LicenseId) || LicenseId.Length > 100 ||
            InstallationId == Guid.Empty || RequestId == Guid.Empty || string.IsNullOrWhiteSpace(Nonce))
            return "El archivo .posreq no tiene una identidad válida.";
        if (CreatedAtUtc.Offset != TimeSpan.Zero || CreatedAtUtc > nowUtc.AddMinutes(5) || CreatedAtUtc < nowUtc.AddDays(-7))
            return "La solicitud debe ser UTC y tener menos de siete días.";
        try
        {
            if (Convert.FromBase64String(Nonce).Length != 32) return "La solicitud contiene un nonce inválido.";
        }
        catch (FormatException) { return "La solicitud contiene un nonce inválido."; }
        return null;
    }
}
public sealed record IssueRecoveryRequest(LicenseRecoveryRequestDocument Request, int DurationHours, string Reason)
{
    public string? Validate()
    {
        if (Request is null) return "Debe incluirse el archivo de solicitud .posreq.";
        var nowUtc = DateTimeOffset.UtcNow;
        var requestError = Request.Validate(nowUtc);
        if (requestError is not null) return requestError;
        if (DurationHours is < 1 or > 48) return "durationHours debe estar entre 1 y 48.";
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 512) return "reason es obligatorio y admite hasta 512 caracteres.";
        return null;
    }
}

public sealed record CreateSubscriptionRequest(Guid InstallationId, string CustomerRif, string CustomerName, string Plan,
    int MaxTerminals, IReadOnlyList<string> Entitlements, DateTimeOffset PaidThroughUtc,
    decimal? InitialPaymentAmount = null, string? InitialPaymentCurrency = null, string? InitialPaymentReference = null)
{
    private static readonly Regex EntitlementPattern = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public string? Validate()
    {
        if (InstallationId == Guid.Empty) return "installationId debe ser un GUID válido.";
        if (string.IsNullOrWhiteSpace(CustomerRif) || CustomerRif.Length > 32) return "customerRif es obligatorio y admite hasta 32 caracteres.";
        if (string.IsNullOrWhiteSpace(CustomerName) || CustomerName.Length > 200) return "customerName es obligatorio y admite hasta 200 caracteres.";
        if (string.IsNullOrWhiteSpace(Plan) || Plan.Length > 64) return "plan es obligatorio y admite hasta 64 caracteres.";
        if (MaxTerminals is < 1 or > 1000) return "maxTerminals debe estar entre 1 y 1000.";
        if (Entitlements is null || Entitlements.Count > 256 || Entitlements.Any(item => string.IsNullOrWhiteSpace(item) || !EntitlementPattern.IsMatch(item)))
            return "entitlements debe ser una lista de hasta 256 permisos con formato válido.";
        if (Entitlements.Distinct(StringComparer.Ordinal).Count() != Entitlements.Count) return "entitlements no puede repetir valores.";
        if (PaidThroughUtc.Offset != TimeSpan.Zero || PaidThroughUtc <= DateTimeOffset.UtcNow || PaidThroughUtc > DateTimeOffset.UtcNow.AddYears(5))
            return "paidThroughUtc debe estar en UTC, en el futuro y no superar cinco años.";
        if (InitialPaymentAmount.HasValue || InitialPaymentCurrency is not null || InitialPaymentReference is not null)
        {
            if (InitialPaymentAmount is null || InitialPaymentAmount <= 0m || InitialPaymentAmount > 1_000_000_000m)
                return "initialPaymentAmount debe ser mayor que cero y no superar 1.000.000.000.";
            if (string.IsNullOrWhiteSpace(InitialPaymentCurrency) || InitialPaymentCurrency.Length > 8 ||
                !Regex.IsMatch(InitialPaymentCurrency, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
                return "initialPaymentCurrency es obligatorio y admite hasta 8 caracteres alfanuméricos.";
            if (string.IsNullOrWhiteSpace(InitialPaymentReference) || InitialPaymentReference.Length > 128)
                return "initialPaymentReference es obligatorio y admite hasta 128 caracteres.";
        }
        return null;
    }
}

public sealed record LicenseSubscription(string LicenseId, Guid InstallationId, string CustomerRif, string CustomerName,
    string Plan, int MaxTerminals, IReadOnlyList<string> Entitlements, DateTimeOffset PaidThroughUtc,
    string RenewalTokenHash, string RenewalTokenEncrypted, bool Active, long ControlVersion = 0,
    string? SuspensionReason = null, DateTimeOffset? LastSeenAtUtc = null, IReadOnlyList<LicensePayment>? Payments = null,
    IReadOnlyList<LicenseRecoveryIssuance>? RecoveryGrants = null)
{
    public PosLicense ToLicense(DateTimeOffset issuedAtUtc, string? renewalToken = null) => new(
        2, LicenseId, CustomerRif, CustomerName, InstallationId, Plan, MaxTerminals, Entitlements,
        issuedAtUtc, PaidThroughUtc, renewalToken, ControlVersion);
}
public sealed record LicensePayment(Guid PaymentId, DateTimeOffset PaidAtUtc, decimal Amount, string Currency,
    string Reference, DateTimeOffset PaidThroughUtc);
public sealed record LicenseRecoveryIssuance(Guid RequestId, Guid InstallationId, string Nonce,
    DateTimeOffset ExpiresAtUtc, string DocumentBase64);
public sealed record PosLicenseRecoveryGrantDocument(int SchemaVersion, string LicenseId, Guid InstallationId,
    Guid RequestId, string Nonce, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string Reason);

public sealed record PosLicense(int SchemaVersion, string LicenseId, string CustomerRif, string CustomerName,
    Guid InstallationId, string Plan, int MaxTerminals, IReadOnlyList<string> Entitlements,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string? RenewalToken, long ControlVersion = 0);

public sealed class PosLicenseIssuer : IDisposable
{
    private static readonly byte[] SignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-LICENSE.v1\n");
    private static readonly byte[] ControlSignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-CONTROL.v1\n");
    private static readonly byte[] RecoverySignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-RECOVERY.v1\n");
    private readonly RSA _rsa = RSA.Create();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PosLicenseIssuer(string keyId, string privateKeyPath)
    {
        KeyId = keyId;
        _rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        if (_rsa.KeySize < 2048) throw new InvalidOperationException("La llave privada debe tener al menos 2048 bits.");
        PublicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem();
    }

    public string KeyId { get; }
    public string PublicKeyPem { get; }

    public string EncryptRenewalToken(string token) => Convert.ToBase64String(
        _rsa.Encrypt(Encoding.UTF8.GetBytes(token), RSAEncryptionPadding.OaepSHA256));

    public string DecryptRenewalToken(string encryptedToken)
    {
        try { return Encoding.UTF8.GetString(_rsa.Decrypt(Convert.FromBase64String(encryptedToken), RSAEncryptionPadding.OaepSHA256)); }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidDataException("No se pudo descifrar la credencial de renovación guardada en el VPS.", exception);
        }
    }

    public byte[] Issue(PosLicense license)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(license, JsonOptions);
        return IssueEnvelope(payload, SignatureContext);
    }

    public byte[] IssueControl(PosLicenseControlDocument control)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(control, JsonOptions);
        return IssueEnvelope(payload, ControlSignatureContext);
    }

    public byte[] IssueRecovery(PosLicenseRecoveryGrantDocument grant)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(grant, JsonOptions);
        return IssueEnvelope(payload, RecoverySignatureContext);
    }

    private byte[] IssueEnvelope(byte[] payload, byte[] signatureContext)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(KeyId + "\n");
        var signedBytes = new byte[signatureContext.Length + keyIdBytes.Length + payload.Length];
        signatureContext.CopyTo(signedBytes, 0);
        keyIdBytes.CopyTo(signedBytes, signatureContext.Length);
        payload.CopyTo(signedBytes, signatureContext.Length + keyIdBytes.Length);
        var signature = _rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.SerializeToUtf8Bytes(new LicenseEnvelope(1, KeyId,
            Convert.ToBase64String(payload), Convert.ToBase64String(signature)), JsonOptions);
    }

    public void Dispose() => _rsa.Dispose();
    private sealed record LicenseEnvelope(int SchemaVersion, string KeyId, string Payload, string Signature);
}

public sealed record LicenseServiceOptions(string KeyId, string PrivateKeyPath, string StorePath, string AuditPath, string AdminToken)
{
    public static LicenseServiceOptions Load(IConfiguration configuration)
    {
        var keyId = configuration["LicensingVps:KeyId"] ?? "pos-publisher-vps-2026";
        if (!Regex.IsMatch(keyId, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("LicensingVps:KeyId no tiene un formato válido.");
        var privateKeyPath = configuration["LicensingVps:PrivateKeyPath"] ?? "/etc/pos-license-api/license-private.pem";
        var storePath = configuration["LicensingVps:StorePath"] ?? "/var/lib/pos-license-api/subscriptions.json";
        var auditPath = configuration["LicensingVps:AuditPath"] ?? "/var/lib/pos-license-api/audit.jsonl";
        var adminToken = configuration["LicensingVps:AdminToken"];
        var adminTokenFile = configuration["LicensingVps:AdminTokenFile"];
        if (!string.IsNullOrWhiteSpace(adminTokenFile))
        {
            if (!Path.IsPathFullyQualified(adminTokenFile))
                throw new InvalidOperationException("LicensingVps:AdminTokenFile debe ser una ruta absoluta.");
            if (!File.Exists(adminTokenFile))
                throw new FileNotFoundException("No se encontró el archivo del token administrativo.", adminTokenFile);
            adminToken = File.ReadAllText(adminTokenFile).TrimEnd('\r', '\n');
        }
        if (string.IsNullOrWhiteSpace(adminToken) || adminToken.Length < 32)
            throw new InvalidOperationException("Configure un token administrativo con al menos 32 caracteres mediante LicensingVps:AdminToken o LicensingVps:AdminTokenFile.");
        if (!Path.IsPathFullyQualified(privateKeyPath) || !Path.IsPathFullyQualified(storePath) || !Path.IsPathFullyQualified(auditPath))
            throw new InvalidOperationException("Las rutas de llave privada, suscripciones y auditoría deben ser absolutas.");
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException("No se encontró la llave privada de licencias.", privateKeyPath);
        return new LicenseServiceOptions(keyId, privateKeyPath, storePath, auditPath, adminToken);
    }
}

public sealed class LicenseSubscriptionStore(string path)
{
    private const int MaximumStoreBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LicenseSubscription?> FindAsync(string licenseId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAsync(cancellationToken)).FirstOrDefault(item => string.Equals(item.LicenseId, licenseId, StringComparison.Ordinal)); }
        finally { _gate.Release(); }
    }

    public async Task<LicenseSubscription?> FindByInstallationAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAsync(cancellationToken)).FirstOrDefault(item => item.InstallationId == installationId); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LicenseSubscription>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAsync(cancellationToken)).OrderBy(item => item.CustomerName, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(LicenseSubscription subscription, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadAsync(cancellationToken);
            if (records.Any(item => item.InstallationId == subscription.InstallationId))
                throw new InvalidOperationException("Ya existe una suscripción para esta instalación.");
            records.Add(subscription);
            await WriteAsync(records, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<LicenseSubscription?> SetPaidThroughAsync(string licenseId, DateTimeOffset paidThroughUtc, CancellationToken cancellationToken) =>
        await UpdateAsync(licenseId, item => item with { PaidThroughUtc = paidThroughUtc, ControlVersion = item.ControlVersion + 1 }, cancellationToken);

    public async Task<LicenseSubscription?> RecordPaymentAsync(string licenseId, LicensePayment payment, CancellationToken cancellationToken) =>
        await UpdateAsync(licenseId, item => item with
        {
            PaidThroughUtc = payment.PaidThroughUtc,
            ControlVersion = item.ControlVersion + 1,
            Payments = (item.Payments ?? []).Append(payment).ToArray()
        }, cancellationToken);

    public async Task<LicenseSubscription?> UpdateTermsAsync(string licenseId, UpdateLicenseTermsRequest request,
        CancellationToken cancellationToken) => await UpdateAsync(licenseId, item => item with
        {
            Plan = request.Plan.Trim(),
            MaxTerminals = request.MaxTerminals,
            Entitlements = request.Entitlements.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ControlVersion = item.ControlVersion + 1
        }, cancellationToken);

    public async Task<(LicenseRecoveryIssuance? Issuance, bool Created)> GetOrCreateRecoveryGrantAsync(string licenseId,
        LicenseRecoveryRequestDocument request, byte[] signedDocument, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadAsync(cancellationToken);
            var index = records.FindIndex(item => string.Equals(item.LicenseId, licenseId, StringComparison.Ordinal));
            if (index < 0) return (null, false);
            var subscription = records[index];
            var issued = subscription.RecoveryGrants ?? [];
            var existing = issued.FirstOrDefault(item => item.RequestId == request.RequestId);
            if (existing is not null)
                return existing.InstallationId == request.InstallationId && RecoveryNonceMatches(existing.Nonce, request.Nonce)
                    ? (existing, false)
                    : (null, false);
            if (issued.Count >= 1000) return (null, false);

            var entry = new LicenseRecoveryIssuance(request.RequestId, request.InstallationId, request.Nonce,
                expiresAtUtc, Convert.ToBase64String(signedDocument));
            records[index] = subscription with { RecoveryGrants = issued.Append(entry).ToArray() };
            await WriteAsync(records, cancellationToken);
            return (entry, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<LicenseSubscription?> SetActiveAsync(string licenseId, bool active, string? reason, CancellationToken cancellationToken) =>
        await UpdateAsync(licenseId, item => item.Active == active && item.SuspensionReason == (active ? null : reason)
            ? item
            : item with { Active = active, SuspensionReason = active ? null : reason, ControlVersion = item.ControlVersion + 1 }, cancellationToken);

    public async Task<LicenseSubscription?> EnsureSuspensionVersionAsync(string licenseId, CancellationToken cancellationToken) =>
        await UpdateAsync(licenseId, item => !item.Active && item.ControlVersion <= 0
            ? item with { ControlVersion = 1, SuspensionReason = item.SuspensionReason ?? "Suspensión administrativa" }
            : item, cancellationToken);

    public async Task<LicenseSubscription?> TouchLastSeenAsync(string licenseId, DateTimeOffset lastSeenAtUtc, CancellationToken cancellationToken) =>
        await UpdateAsync(licenseId, item => item with { LastSeenAtUtc = lastSeenAtUtc }, cancellationToken);

    private async Task<LicenseSubscription?> UpdateAsync(string licenseId, Func<LicenseSubscription, LicenseSubscription> update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadAsync(cancellationToken);
            var index = records.FindIndex(item => string.Equals(item.LicenseId, licenseId, StringComparison.Ordinal));
            if (index < 0) return null;
            records[index] = update(records[index]);
            await WriteAsync(records, cancellationToken);
            return records[index];
        }
        finally { _gate.Release(); }
    }

    private async Task<List<LicenseSubscription>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumStoreBytes) throw new InvalidDataException("El archivo de suscripciones está vacío o supera 10 MiB.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<List<LicenseSubscription>>(input, JsonOptions, cancellationToken) ?? [];
    }

    private static bool RecoveryNonceMatches(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromBase64String(left);
            var rightBytes = Convert.FromBase64String(right);
            return leftBytes.Length == 32 && rightBytes.Length == 32 && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException) { return false; }
    }

    private async Task WriteAsync(List<LicenseSubscription> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, records, JsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

public sealed class LicenseAuditStore(string path)
{
    private const long MaximumCurrentLogBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<LicenseAuditEntry>> ReadRecentAsync(int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path)) return [];
            var info = new FileInfo(path);
            if (info.Length > MaximumCurrentLogBytes + 1024 * 1024)
                throw new InvalidDataException("El registro de auditoría supera el tamaño máximo.");
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var result = new List<LicenseAuditEntry>(take);
            foreach (var line in lines.Reverse().Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<LicenseAuditEntry>(line, JsonOptions);
                    if (entry is not null) result.Add(entry);
                }
                catch (JsonException) { }
                if (result.Count == take) break;
            }
            result.Reverse();
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(string action, string licenseId, Guid installationId, object details,
        CancellationToken cancellationToken)
    {
        var entry = new LicenseAuditEntry(DateTimeOffset.UtcNow, "vps-admin", action, licenseId, installationId,
            JsonSerializer.SerializeToElement(details, JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumCurrentLogBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path + ".1", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await output.WriteAsync(bytes, cancellationToken);
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        finally { _gate.Release(); }
    }
}

public sealed record LicenseAuditEntry(DateTimeOffset OccurredAtUtc, string Actor, string Action, string LicenseId,
    Guid InstallationId, JsonElement Details);

public static class LicenseSecrets
{
    public static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string expectedHash)
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException) { return false; }
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool HasAdminAccess(HttpContext context, string expectedToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var providedBytes = Encoding.UTF8.GetBytes(header[prefix.Length..]);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return providedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
