using System.Text.Json;
using System.Text.Json.Serialization;
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
    string? Diagnostic);

public sealed record PosLicenseRenewalRequest(string LicenseId, Guid InstallationId, string RenewalToken);

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
    private readonly PosLicenseDocumentVerifier? _verifier;
    private readonly PosLicenseClockGuard? _clockGuard;
    private readonly SemaphoreSlim _installationGate = new(1, 1);
    private readonly SemaphoreSlim _licenseGate = new(1, 1);

    public PosLicenseManager(string dataDirectory, string? trustKeyRingPath = null, PosLicenseClockGuard? clockGuard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("La carpeta de licencias debe ser una ruta absoluta.", nameof(dataDirectory));

        _dataDirectory = Path.GetFullPath(dataDirectory);
        _installationIdPath = Path.Combine(_dataDirectory, "installation-id.txt");
        _licensePath = Path.Combine(_dataDirectory, "license.poslic");
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
            _verifier = new PosLicenseDocumentVerifier(LoadTrustedPublicKeys(resolvedTrustKeyRingPath));
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
            var state = PosLicensePolicy.Evaluate(license, nowUtc);
            return new PosLicenseStatus(installationId, state.ToString(), license.LicenseId, license.CustomerName,
                license.Plan, license.MaxTerminals, license.ExpiresAtUtc, license.Entitlements, null);
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
        return status.State is "Active" or "Grace"
            ? new HashSet<string>(status.Entitlements, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
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

        return new PosLicenseRenewalRequest(license.LicenseId, installationId, license.RenewalToken);
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
        if (PosLicensePolicy.Evaluate(license, nowUtc) == PosLicenseState.Locked)
            throw new PosLicenseDocumentException("La licencia ya terminó su periodo de gracia y no puede instalarse como renovación.");

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
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _licenseGate.Release();
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

    private sealed record PosLicenseKeyRingDocument(int SchemaVersion, IReadOnlyList<PosLicensePublicKey>? Keys);
    private sealed record PosLicensePublicKey(string KeyId, string PublicKeyPem);
}
