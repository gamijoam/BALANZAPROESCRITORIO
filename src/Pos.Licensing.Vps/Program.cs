using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var options = LicenseServiceOptions.Load(builder.Configuration);
var issuer = new PosLicenseIssuer(options.KeyId, options.PrivateKeyPath);
var store = new LicenseSubscriptionStore(options.StorePath);
builder.Services.AddSingleton(issuer);
builder.Services.AddSingleton(store);
var app = builder.Build();

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
    if (!subscription.Active || subscription.PaidThroughUtc <= nowUtc)
        return Results.NoContent();

    var renewalToken = licenseIssuer.DecryptRenewalToken(subscription.RenewalTokenEncrypted);
    var document = licenseIssuer.Issue(subscription.ToLicense(nowUtc, renewalToken));
    return Results.Ok(new RenewalResponse(Convert.ToBase64String(document), subscription.PaidThroughUtc));
});

app.MapPost("/api/v1/admin/licenses", async (CreateSubscriptionRequest request, HttpContext context,
    LicenseSubscriptionStore subscriptions, PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
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
        request.PaidThroughUtc, LicenseSecrets.HashToken(renewalToken), licenseIssuer.EncryptRenewalToken(renewalToken), Active: true);
    try { await subscriptions.AddAsync(subscription, cancellationToken); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }

    var document = licenseIssuer.Issue(subscription.ToLicense(nowUtc, renewalToken));
    return Results.Created($"/api/v1/admin/licenses/{subscription.LicenseId}", new
    {
        licenseId = subscription.LicenseId,
        installationId = subscription.InstallationId,
        expiresAtUtc = subscription.PaidThroughUtc,
        documentBase64 = Convert.ToBase64String(document)
    });
});

app.MapGet("/api/v1/admin/installations/{installationId:guid}/license-document", async (Guid installationId,
    HttpContext context, LicenseSubscriptionStore subscriptions, PosLicenseIssuer licenseIssuer, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    var subscription = await subscriptions.FindByInstallationAsync(installationId, cancellationToken);
    if (subscription is null) return Results.NotFound();
    var token = licenseIssuer.DecryptRenewalToken(subscription.RenewalTokenEncrypted);
    var document = licenseIssuer.Issue(subscription.ToLicense(DateTimeOffset.UtcNow, token));
    return Results.Ok(new { licenseId = subscription.LicenseId, installationId, expiresAtUtc = subscription.PaidThroughUtc,
        documentBase64 = Convert.ToBase64String(document) });
});

app.MapPut("/api/v1/admin/licenses/{licenseId}/paid-through", async (string licenseId, PaidThroughRequest request,
    HttpContext context, LicenseSubscriptionStore subscriptions, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    if (request.PaidThroughUtc <= DateTimeOffset.UtcNow || request.PaidThroughUtc > DateTimeOffset.UtcNow.AddYears(5))
        return Results.BadRequest(new { error = "paidThroughUtc debe estar en el futuro y no superar cinco años." });

    var updated = await subscriptions.SetPaidThroughAsync(licenseId, request.PaidThroughUtc, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, paidThroughUtc = updated.PaidThroughUtc });
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/disable", async (string licenseId, HttpContext context,
    LicenseSubscriptionStore subscriptions, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    var updated = await subscriptions.SetActiveAsync(licenseId, active: false, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, active = false });
});

app.MapPost("/api/v1/admin/licenses/{licenseId}/enable", async (string licenseId, HttpContext context,
    LicenseSubscriptionStore subscriptions, CancellationToken cancellationToken) =>
{
    if (!LicenseSecrets.HasAdminAccess(context, options.AdminToken))
        return Results.Unauthorized();
    var updated = await subscriptions.SetActiveAsync(licenseId, active: true, cancellationToken);
    return updated is null ? Results.NotFound() : Results.Ok(new { licenseId, active = true });
});

app.Run();

public sealed record RenewalRequest(string LicenseId, Guid InstallationId, string RenewalToken);
public sealed record RenewalResponse(string DocumentBase64, DateTimeOffset ExpiresAtUtc);
public sealed record PaidThroughRequest(DateTimeOffset PaidThroughUtc);

public sealed record CreateSubscriptionRequest(Guid InstallationId, string CustomerRif, string CustomerName, string Plan,
    int MaxTerminals, IReadOnlyList<string> Entitlements, DateTimeOffset PaidThroughUtc)
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
        return null;
    }
}

public sealed record LicenseSubscription(string LicenseId, Guid InstallationId, string CustomerRif, string CustomerName,
    string Plan, int MaxTerminals, IReadOnlyList<string> Entitlements, DateTimeOffset PaidThroughUtc,
    string RenewalTokenHash, string RenewalTokenEncrypted, bool Active)
{
    public PosLicense ToLicense(DateTimeOffset issuedAtUtc, string? renewalToken = null) => new(
        1, LicenseId, CustomerRif, CustomerName, InstallationId, Plan, MaxTerminals, Entitlements,
        issuedAtUtc, PaidThroughUtc, renewalToken);
}

public sealed record PosLicense(int SchemaVersion, string LicenseId, string CustomerRif, string CustomerName,
    Guid InstallationId, string Plan, int MaxTerminals, IReadOnlyList<string> Entitlements,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string? RenewalToken);

public sealed class PosLicenseIssuer : IDisposable
{
    private static readonly byte[] SignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-LICENSE.v1\n");
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
        var keyIdBytes = Encoding.UTF8.GetBytes(KeyId + "\n");
        var signedBytes = new byte[SignatureContext.Length + keyIdBytes.Length + payload.Length];
        SignatureContext.CopyTo(signedBytes, 0);
        keyIdBytes.CopyTo(signedBytes, SignatureContext.Length);
        payload.CopyTo(signedBytes, SignatureContext.Length + keyIdBytes.Length);
        var signature = _rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.SerializeToUtf8Bytes(new LicenseEnvelope(1, KeyId,
            Convert.ToBase64String(payload), Convert.ToBase64String(signature)), JsonOptions);
    }

    public void Dispose() => _rsa.Dispose();
    private sealed record LicenseEnvelope(int SchemaVersion, string KeyId, string Payload, string Signature);
}

public sealed record LicenseServiceOptions(string KeyId, string PrivateKeyPath, string StorePath, string AdminToken)
{
    public static LicenseServiceOptions Load(IConfiguration configuration)
    {
        var keyId = configuration["LicensingVps:KeyId"] ?? "pos-publisher-vps-2026";
        if (!Regex.IsMatch(keyId, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("LicensingVps:KeyId no tiene un formato válido.");
        var privateKeyPath = configuration["LicensingVps:PrivateKeyPath"] ?? "/etc/pos-license-api/license-private.pem";
        var storePath = configuration["LicensingVps:StorePath"] ?? "/var/lib/pos-license-api/subscriptions.json";
        var adminToken = configuration["LicensingVps:AdminToken"];
        if (string.IsNullOrWhiteSpace(adminToken) || adminToken.Length < 32)
            throw new InvalidOperationException("Configure LicensingVps:AdminToken con al menos 32 caracteres mediante un secreto del sistema.");
        if (!Path.IsPathFullyQualified(privateKeyPath) || !Path.IsPathFullyQualified(storePath))
            throw new InvalidOperationException("Las rutas de llave privada y suscripciones deben ser absolutas.");
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException("No se encontró la llave privada de licencias.", privateKeyPath);
        return new LicenseServiceOptions(keyId, privateKeyPath, storePath, adminToken);
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
        await UpdateAsync(licenseId, item => item with { PaidThroughUtc = paidThroughUtc }, cancellationToken);

    public async Task<LicenseSubscription?> SetActiveAsync(string licenseId, bool active, CancellationToken cancellationToken) =>
        await UpdateAsync(licenseId, item => item with { Active = active }, cancellationToken);

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
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

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
