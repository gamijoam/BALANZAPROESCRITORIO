using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Pos.Application.Licensing;
using Pos.Domain.Licensing;

namespace Pos.Infrastructure.Licensing;

public sealed record PosLicenseStatus(
    Guid InstallationId,
    string State,
    string? LicenseId,
    string? CustomerName,
    string? Plan,
    int? MaxTerminals,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<string> Entitlements,
    string? Diagnostic,
    long ControlVersion = 0,
    DateTimeOffset? SuspendedAtUtc = null,
    DateTimeOffset? RecoveryExpiresAtUtc = null);

public sealed record PosLicenseRenewalRequest(string LicenseId, Guid InstallationId, string RenewalToken, int ProtocolVersion = 2);

/// <summary>Loads and replaces the signed license stored by the local POS server.</summary>
public sealed class PosLicenseManager
{
    private const int MaxTrustKeyRingBytes = 1024 * 1024;
    private const int MaxLicenseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions KeyRingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private readonly string _dataDirectory;
    private readonly string _installationIdPath;
    private readonly string _licensePath;
    private readonly string _controlPath;
    private readonly string _recoveryRequestPath;
    private readonly string _recoveryActiveRequestPath;
    private readonly string _recoveryGrantPath;
    private readonly PosLicenseDocumentVerifier? _verifier;
    private readonly PosLicenseControlDocumentVerifier? _controlVerifier;
    private readonly PosLicenseRecoveryGrantVerifier? _recoveryGrantVerifier;
    private readonly PosLicenseClockGuard? _clockGuard;
    private readonly SemaphoreSlim _installationGate = new(1, 1);
    private readonly SemaphoreSlim _licenseGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    public PosLicenseManager(string dataDirectory, string? trustKeyRingPath = null, PosLicenseClockGuard? clockGuard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("La carpeta de licencias debe ser una ruta absoluta.", nameof(dataDirectory));

        _dataDirectory = Path.GetFullPath(dataDirectory);
        _installationIdPath = Path.Combine(_dataDirectory, "installation-id.txt");
        _licensePath = Path.Combine(_dataDirectory, "license.poslic");
        _controlPath = Path.Combine(_dataDirectory, "license-control.posctl");
        _recoveryRequestPath = Path.Combine(_dataDirectory, "license-recovery-pending.posreq");
        _recoveryActiveRequestPath = Path.Combine(_dataDirectory, "license-recovery-active.posreq");
        _recoveryGrantPath = Path.Combine(_dataDirectory, "license-recovery.posunlock");
        _clockGuard = clockGuard ?? (OperatingSystem.IsWindows()
            ? new PosLicenseClockGuard(_dataDirectory, new WindowsDpapiLicenseClockProtector())
            : null);

        var trustKeyRingWasConfigured = !string.IsNullOrWhiteSpace(trustKeyRingPath);
        var resolvedTrustKeyRingPath = !trustKeyRingWasConfigured
            ? Path.Combine(_dataDirectory, "trusted-license-keys.json")
            : Environment.ExpandEnvironmentVariables(trustKeyRingPath!);
        if (!Path.IsPathFullyQualified(resolvedTrustKeyRingPath))
            throw new ArgumentException("El archivo de llaves públicas de licencias debe tener una ruta absoluta.", nameof(trustKeyRingPath));
        if (trustKeyRingWasConfigured && !File.Exists(resolvedTrustKeyRingPath))
            throw new FileNotFoundException("No se encontró el archivo de llaves públicas configurado para licencias.", resolvedTrustKeyRingPath);
        if (File.Exists(resolvedTrustKeyRingPath))
        {
            var publicKeys = LoadTrustedPublicKeys(resolvedTrustKeyRingPath);
            _verifier = new PosLicenseDocumentVerifier(publicKeys);
            _controlVerifier = new PosLicenseControlDocumentVerifier(publicKeys);
            _recoveryGrantVerifier = new PosLicenseRecoveryGrantVerifier(publicKeys);
        }
    }

    public Guid GetInstallationId()
    {
        _installationGate.Wait();
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            if (File.Exists(_installationIdPath))
                return ReadInstallationId();

            var installationId = Guid.NewGuid();
            try
            {
                using var output = new FileStream(_installationIdPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough);
                using var writer = new StreamWriter(output, leaveOpen: true);
                writer.Write(installationId.ToString("D"));
                writer.Flush();
                output.Flush(flushToDisk: true);
                return installationId;
            }
            catch (IOException) when (File.Exists(_installationIdPath))
            {
                return ReadInstallationId();
            }
        }
        finally
        {
            _installationGate.Release();
        }
    }

    public PosLicenseStatus GetStatus(DateTimeOffset nowUtc)
    {
        var installationId = GetInstallationId();
        if (_clockGuard?.CheckAndRecord(nowUtc) is { } clockDiagnostic)
            return EmptyStatus(installationId, "ClockRollback", clockDiagnostic);
        if (_verifier is null)
            return EmptyStatus(installationId, "NotConfigured", "No hay llaves públicas de licencias configuradas.");
        if (!File.Exists(_licensePath))
            return EmptyStatus(installationId, "Missing", "No hay una licencia instalada.");

        try
        {
            var document = ReadBoundedFile(_licensePath, MaxLicenseBytes, "archivo de licencia");
            var license = _verifier.Verify(document, installationId, nowUtc);
            PosLicenseStatus status;
            if (File.Exists(_controlPath))
            {
                var controlBytes = ReadBoundedFile(_controlPath, 32 * 1024, "orden de control");
                var control = _controlVerifier?.Verify(controlBytes, installationId, license.LicenseId, -1, nowUtc)
                    ?? throw new PosLicenseDocumentException("No hay una llave pública para validar la orden de control.");
                if (control.ControlVersion > license.ControlVersion)
                {
                    status = new PosLicenseStatus(installationId, "Suspended", license.LicenseId, license.CustomerName,
                        license.Plan, license.MaxTerminals, license.ExpiresAtUtc, [],
                        string.IsNullOrWhiteSpace(control.Reason) ? "La suscripción fue suspendida desde el VPS." : control.Reason,
                        control.ControlVersion, control.IssuedAtUtc);
                }
                else
                {
                    var activeState = PosLicensePolicy.Evaluate(license, nowUtc);
                    status = new PosLicenseStatus(installationId, activeState.ToString(), license.LicenseId, license.CustomerName,
                        license.Plan, license.MaxTerminals, license.ExpiresAtUtc, license.Entitlements, null,
                        license.ControlVersion, null);
                }
            }
            else
            {
                var activeState = PosLicensePolicy.Evaluate(license, nowUtc);
                status = new PosLicenseStatus(installationId, activeState.ToString(), license.LicenseId, license.CustomerName,
                    license.Plan, license.MaxTerminals, license.ExpiresAtUtc, license.Entitlements, null,
                    license.ControlVersion, null);
            }

            if ((status.State is "Locked" or "Suspended") && File.Exists(_recoveryGrantPath) && File.Exists(_recoveryActiveRequestPath))
            {
                var request = DeserializeRecoveryRequest(ReadBoundedFile(_recoveryActiveRequestPath, 16 * 1024, "solicitud de desbloqueo"));
                var grantBytes = ReadBoundedFile(_recoveryGrantPath, 32 * 1024, "archivo de desbloqueo");
                var grant = _recoveryGrantVerifier?.Verify(grantBytes, request, nowUtc, requireCurrent: false)
                    ?? throw new PosLicenseDocumentException("No hay una llave pública para validar el desbloqueo.");
                if (grant.ExpiresAtUtc > nowUtc)
                    return new PosLicenseStatus(installationId, "Recovery", license.LicenseId, license.CustomerName,
                        license.Plan, license.MaxTerminals, license.ExpiresAtUtc, license.Entitlements,
                        $"Desbloqueo temporal vigente hasta {grant.ExpiresAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}.",
                        license.ControlVersion, status.SuspendedAtUtc, grant.ExpiresAtUtc);
            }
            return status;
        }
        catch (PosLicenseDocumentException exception)
        {
            return EmptyStatus(installationId, "Invalid", exception.Message);
        }
        catch (IOException exception)
        {
            return EmptyStatus(installationId, "Invalid", $"No se pudo leer la licencia: {exception.Message}");
        }
    }

    public IReadOnlySet<string> GetGrantedEntitlements(DateTimeOffset nowUtc)
    {
        var status = GetStatus(nowUtc);
        return status.State is "Active" or "Grace" or "Recovery"
            ? new HashSet<string>(status.Entitlements, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public byte[] CreateRecoveryRequest(DateTimeOffset nowUtc)
    {
        _recoveryGate.Wait();
        try
        {
            var status = GetStatus(nowUtc);
            if (status.State is not ("Locked" or "Suspended"))
                throw new PosLicenseDocumentException("La solicitud de desbloqueo solo está disponible si la licencia está bloqueada o suspendida.");
            if (_verifier is null || !File.Exists(_licensePath) || status.LicenseId is null)
                throw new PosLicenseDocumentException("No hay una licencia válida para solicitar un desbloqueo.");
            var installationId = GetInstallationId();
            var license = _verifier.Verify(ReadBoundedFile(_licensePath, MaxLicenseBytes, "archivo de licencia"), installationId, nowUtc);
            var request = new PosLicenseRecoveryRequest(1, license.LicenseId, installationId, Guid.NewGuid(),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), nowUtc);
            var document = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            WriteAtomic(_recoveryRequestPath, document);
            if (File.Exists(_recoveryActiveRequestPath)) File.Delete(_recoveryActiveRequestPath);
            if (File.Exists(_recoveryGrantPath)) File.Delete(_recoveryGrantPath);
            return document;
        }
        finally { _recoveryGate.Release(); }
    }

    public PosLicenseStatus ApplyRecoveryGrant(ReadOnlySpan<byte> document, DateTimeOffset nowUtc)
    {
        _recoveryGate.Wait();
        try
        {
            if (_verifier is null || _recoveryGrantVerifier is null || !File.Exists(_licensePath) || !File.Exists(_recoveryRequestPath))
                throw new PosLicenseDocumentException("No hay una solicitud de desbloqueo pendiente para esta instalación.");
            var installationId = GetInstallationId();
            var license = _verifier.Verify(ReadBoundedFile(_licensePath, MaxLicenseBytes, "archivo de licencia"), installationId, nowUtc);
            var request = DeserializeRecoveryRequest(ReadBoundedFile(_recoveryRequestPath, 16 * 1024, "solicitud de desbloqueo"));
            if (request.LicenseId != license.LicenseId || request.InstallationId != installationId ||
                request.CreatedAtUtc.Offset != TimeSpan.Zero || request.CreatedAtUtc > nowUtc.AddMinutes(5) ||
                request.CreatedAtUtc < nowUtc.AddDays(-7))
                throw new PosLicenseDocumentException("La solicitud local de desbloqueo no es válida o ya venció.");
            _ = _recoveryGrantVerifier.Verify(document, request, nowUtc);
            WriteAtomic(_recoveryActiveRequestPath, ReadBoundedFile(_recoveryRequestPath, 16 * 1024, "solicitud de desbloqueo"));
            WriteAtomic(_recoveryGrantPath, document);
            File.Delete(_recoveryRequestPath);
            return GetStatus(nowUtc);
        }
        finally { _recoveryGate.Release(); }
    }

    public PosLicenseRenewalRequest? CreateRenewalRequest(DateTimeOffset nowUtc)
    {
        var status = GetStatus(nowUtc);
        if (_verifier is null || status.LicenseId is null || !File.Exists(_licensePath))
            return null;

        var installationId = GetInstallationId();
        var license = _verifier.Verify(ReadBoundedFile(_licensePath, MaxLicenseBytes, "archivo de licencia"), installationId, nowUtc);
        if (string.IsNullOrWhiteSpace(license.RenewalToken) || license.RenewalToken.Length > 256)
            return null;

        return new PosLicenseRenewalRequest(license.LicenseId, installationId, license.RenewalToken, 2);
    }

    public PosLicenseStatus ApplyControlDocument(ReadOnlySpan<byte> document, DateTimeOffset nowUtc)
    {
        if (_verifier is null || _controlVerifier is null || !File.Exists(_licensePath))
            throw new PosLicenseDocumentException("No se puede aplicar una suspensión sin una licencia y una llave pública configuradas.");
        var installationId = GetInstallationId();
        var license = _verifier.Verify(ReadBoundedFile(_licensePath, MaxLicenseBytes, "archivo de licencia"), installationId, nowUtc);
        var currentVersion = license.ControlVersion;
        if (File.Exists(_controlPath))
        {
            var currentControl = _controlVerifier.Verify(ReadBoundedFile(_controlPath, 32 * 1024, "orden de control"),
                installationId, license.LicenseId, -1, nowUtc);
            currentVersion = Math.Max(currentVersion, currentControl.ControlVersion);
        }

        var control = _controlVerifier.Verify(document, installationId, license.LicenseId, -1, nowUtc);
        if (control.ControlVersion <= currentVersion)
            return GetStatus(nowUtc);
        WriteAtomic(_controlPath, document);
        return new PosLicenseStatus(installationId, "Suspended", license.LicenseId, license.CustomerName,
            license.Plan, license.MaxTerminals, license.ExpiresAtUtc, [],
            string.IsNullOrWhiteSpace(control.Reason) ? "La suscripción fue suspendida desde el VPS." : control.Reason,
            control.ControlVersion, control.IssuedAtUtc);
    }

    public PosLicenseStatus Install(ReadOnlySpan<byte> document, DateTimeOffset nowUtc)
    {
        if (_clockGuard?.CheckAndRecord(nowUtc) is { } clockDiagnostic)
            throw new PosLicenseDocumentException(clockDiagnostic);
        if (_verifier is null)
            throw new PosLicenseDocumentException("No hay llaves públicas de licencias configuradas en el servidor.");
        if (document.IsEmpty || document.Length > MaxLicenseBytes)
            throw new PosLicenseDocumentException("El archivo de licencia está vacío o supera el tamaño permitido.");

        var installationId = GetInstallationId();
        var license = _verifier.Verify(document, installationId, nowUtc);
        PosLicenseControlDocument? currentControl = null;
        if (File.Exists(_controlPath))
        {
            currentControl = _controlVerifier?.Verify(ReadBoundedFile(_controlPath, 32 * 1024, "orden de control"),
                installationId, license.LicenseId, -1, nowUtc);
            if (currentControl is not null && currentControl.ControlVersion > license.ControlVersion)
                throw new PosLicenseDocumentException("La licencia recibida es anterior a una suspensión remota vigente.");
        }
        if (PosLicensePolicy.Evaluate(license, nowUtc) == PosLicenseState.Locked)
            throw new PosLicenseDocumentException("La licencia ya terminó su periodo de gracia y no puede instalarse como renovación.");

        _recoveryGate.Wait();
        _licenseGate.Wait();
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var temporaryPath = Path.Combine(_dataDirectory, $"license.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 8192, FileOptions.WriteThrough))
                {
                    output.Write(document);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _licensePath, overwrite: true);
                if (currentControl is not null && license.ControlVersion >= currentControl.ControlVersion && File.Exists(_controlPath))
                    File.Delete(_controlPath);
                DeleteIfExists(_recoveryRequestPath);
                DeleteIfExists(_recoveryActiveRequestPath);
                DeleteIfExists(_recoveryGrantPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _licenseGate.Release();
            _recoveryGate.Release();
        }

        return GetStatus(nowUtc);
    }

    private Guid ReadInstallationId()
    {
        var text = File.ReadAllText(_installationIdPath).Trim();
        return Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty
            ? value
            : throw new InvalidDataException("El identificador local de la instalación está dañado.");
    }

    private static IReadOnlyDictionary<string, string> LoadTrustedPublicKeys(string path)
    {
        var bytes = ReadBoundedFile(path, MaxTrustKeyRingBytes, "archivo de llaves públicas");
        try
        {
            var keyRing = JsonSerializer.Deserialize<PosLicenseKeyRingDocument>(bytes, KeyRingJsonOptions)
                ?? throw new InvalidDataException("El archivo de llaves públicas está vacío.");
            if (keyRing.SchemaVersion != 1 || keyRing.Keys is null || keyRing.Keys.Count == 0)
                throw new InvalidDataException("El archivo de llaves públicas no tiene una versión compatible o está vacío.");

            var publicKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keyRing.Keys)
            {
                if (key is null || string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPem) ||
                    !publicKeys.TryAdd(key.KeyId, key.PublicKeyPem))
                    throw new InvalidDataException("El archivo contiene una llave pública incompleta o un identificador repetido.");
            }
            return publicKeys;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("El archivo de llaves públicas no es JSON válido.", exception);
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes, string description)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException($"El {description} está vacío o supera el tamaño permitido.");
        return File.ReadAllBytes(path);
    }

    private static PosLicenseStatus EmptyStatus(Guid installationId, string state, string diagnostic) =>
        new(installationId, state, null, null, null, null, null, [], diagnostic);

    private static PosLicenseRecoveryRequest DeserializeRecoveryRequest(byte[] bytes)
    {
        try
        {
            var request = JsonSerializer.Deserialize<PosLicenseRecoveryRequest>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new PosLicenseDocumentException("La solicitud de desbloqueo está vacía.");
            var nonce = Convert.FromBase64String(request.Nonce);
            if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.LicenseId) || request.LicenseId.Length > 100 ||
                request.InstallationId == Guid.Empty || request.RequestId == Guid.Empty || nonce.Length != 32)
                throw new PosLicenseDocumentException("La solicitud de desbloqueo no tiene un formato válido.");
            return request;
        }
        catch (PosLicenseDocumentException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new PosLicenseDocumentException($"La solicitud de desbloqueo no es válida: {exception.Message}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                output.Write(contents);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record PosLicenseKeyRingDocument(int SchemaVersion, IReadOnlyList<PosLicensePublicKey>? Keys);
    private sealed record PosLicensePublicKey(string KeyId, string PublicKeyPem);
}
