using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pos.Domain.Licensing;

namespace Pos.Application.Licensing;

public sealed class PosLicenseDocumentException(string message) : Exception(message);

/// <summary>Verifies an offline POS license using public keys only.</summary>
public sealed class PosLicenseDocumentVerifier
{
    private const int MaxDocumentBytes = 64 * 1024;
    private const int MaxPayloadBytes = 48 * 1024;
    private const int MaxSignatureBytes = 16 * 1024;
    private static readonly byte[] SignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-LICENSE.v1\n");
    private static readonly Regex KeyIdPattern = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex EntitlementPattern = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly IReadOnlyDictionary<string, string> _trustedPublicKeys;

    public PosLicenseDocumentVerifier(IReadOnlyDictionary<string, string> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        if (trustedPublicKeys.Count == 0)
            throw new ArgumentException("Debe configurarse al menos una llave pública de licencias.", nameof(trustedPublicKeys));

        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyId, publicKeyPem) in trustedPublicKeys)
        {
            if (string.IsNullOrWhiteSpace(keyId) || !KeyIdPattern.IsMatch(keyId))
                throw new ArgumentException("Una llave de licencia tiene un identificador inválido.", nameof(trustedPublicKeys));
            if (string.IsNullOrWhiteSpace(publicKeyPem))
                throw new ArgumentException($"La llave pública '{keyId}' está vacía.", nameof(trustedPublicKeys));

            using var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(publicKeyPem);
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                throw new ArgumentException($"La llave pública '{keyId}' no se pudo leer.", nameof(trustedPublicKeys), exception);
            }

            if (rsa.KeySize < 2048)
                throw new ArgumentException($"La llave pública '{keyId}' debe tener al menos 2048 bits.", nameof(trustedPublicKeys));
            if (!validated.TryAdd(keyId, publicKeyPem))
                throw new ArgumentException($"El identificador de llave '{keyId}' está repetido.", nameof(trustedPublicKeys));
        }

        _trustedPublicKeys = validated;
    }

    public PosLicense Verify(ReadOnlySpan<byte> documentBytes, Guid installationId, DateTimeOffset nowUtc)
    {
        if (installationId == Guid.Empty)
            throw new ArgumentException("La instalación local requiere un identificador.", nameof(installationId));
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("La hora actual debe estar expresada en UTC.", nameof(nowUtc));
        if (documentBytes.IsEmpty || documentBytes.Length > MaxDocumentBytes)
            throw new PosLicenseDocumentException("El archivo de licencia está vacío o supera el tamaño permitido.");

        try
        {
            using var envelopeDocument = JsonDocument.Parse(documentBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var envelope = envelopeDocument.RootElement;
            EnsureExactProperties(envelope, ["schemaVersion", "keyId", "payload", "signature"], "sobre de licencia");
            if (envelope.GetProperty("schemaVersion").GetInt32() != 1)
                throw new PosLicenseDocumentException("La versión del archivo de licencia no es compatible.");

            var keyId = ReadRequiredString(envelope, "keyId", 64);
            if (!_trustedPublicKeys.TryGetValue(keyId, out var publicKeyPem))
                throw new PosLicenseDocumentException("La licencia fue firmada con una llave no confiable.");

            var payloadBytes = DecodeBase64(ReadRequiredString(envelope, "payload", MaxPayloadBytes * 2), MaxPayloadBytes, "contenido");
            var signature = DecodeBase64(ReadRequiredString(envelope, "signature", MaxSignatureBytes * 2), MaxSignatureBytes, "firma");
            var signedBytes = BuildSignedBytes(keyId, payloadBytes);

            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(publicKeyPem);
                if (!rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new PosLicenseDocumentException("La firma de la licencia no es válida.");
            }

            using var payloadDocument = JsonDocument.Parse(payloadBytes, new JsonDocumentOptions { MaxDepth = 16 });
            var payload = payloadDocument.RootElement;
            var payloadVersion = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("schemaVersion", out var versionElement)
                ? versionElement.GetInt32()
                : 0;
            EnsureExactProperties(payload, payloadVersion == 1
                ? ["schemaVersion", "licenseId", "customerRif", "customerName", "installationId", "plan", "maxTerminals", "entitlements", "issuedAtUtc", "expiresAtUtc", "renewalToken"]
                : ["schemaVersion", "licenseId", "customerRif", "customerName", "installationId", "plan", "maxTerminals", "entitlements", "issuedAtUtc", "expiresAtUtc", "renewalToken", "controlVersion"],
                "contenido de licencia");

            var license = JsonSerializer.Deserialize<PosLicense>(payloadBytes, PayloadJsonOptions)
                ?? throw new PosLicenseDocumentException("No se pudo leer el contenido de la licencia.");
            ValidateClaims(license, installationId, nowUtc);
            return license;
        }
        catch (PosLicenseDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new PosLicenseDocumentException($"El archivo de licencia no es válido: {exception.Message}");
        }
    }

    public static byte[] BuildSignedBytes(string keyId, ReadOnlySpan<byte> payloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var keyBytes = Encoding.UTF8.GetBytes(keyId + "\n");
        var signedBytes = new byte[SignatureContext.Length + keyBytes.Length + payloadBytes.Length];
        SignatureContext.CopyTo(signedBytes, 0);
        keyBytes.CopyTo(signedBytes, SignatureContext.Length);
        payloadBytes.CopyTo(signedBytes.AsSpan(SignatureContext.Length + keyBytes.Length));
        return signedBytes;
    }

    private static void ValidateClaims(PosLicense license, Guid installationId, DateTimeOffset nowUtc)
    {
        if (license.SchemaVersion is not (1 or 2))
            throw new PosLicenseDocumentException("La versión del contenido de licencia no es compatible.");
        if ((license.SchemaVersion == 1 && license.ControlVersion != 0) || license.ControlVersion < 0)
            throw new PosLicenseDocumentException("La versión de control de la licencia no es válida.");
        if (string.IsNullOrWhiteSpace(license.LicenseId) || license.LicenseId.Length > 100)
            throw new PosLicenseDocumentException("La licencia no contiene un identificador válido.");
        if (string.IsNullOrWhiteSpace(license.CustomerName) || license.CustomerName.Length > 200)
            throw new PosLicenseDocumentException("La licencia no contiene un nombre de comercio válido.");
        if (license.CustomerRif is null || license.CustomerRif.Length > 32)
            throw new PosLicenseDocumentException("El RIF del comercio supera el tamaño permitido.");
        if (license.InstallationId == Guid.Empty || license.InstallationId != installationId)
            throw new PosLicenseDocumentException("La licencia pertenece a otra instalación del POS.");
        if (string.IsNullOrWhiteSpace(license.Plan) || license.Plan.Length > 64)
            throw new PosLicenseDocumentException("La licencia no contiene un plan válido.");
        if (license.MaxTerminals is < 1 or > 1000)
            throw new PosLicenseDocumentException("El límite de terminales de la licencia no es válido.");
        if (license.Entitlements is null || license.Entitlements.Count > 256)
            throw new PosLicenseDocumentException("La lista de permisos de la licencia no es válida.");

        var entitlementSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entitlement in license.Entitlements)
        {
            if (string.IsNullOrWhiteSpace(entitlement) || !EntitlementPattern.IsMatch(entitlement) || !entitlementSet.Add(entitlement))
                throw new PosLicenseDocumentException("La licencia contiene un permiso vacío, repetido o inválido.");
        }

        if (license.IssuedAtUtc.Offset != TimeSpan.Zero || license.ExpiresAtUtc.Offset != TimeSpan.Zero)
            throw new PosLicenseDocumentException("Las fechas de emisión y vencimiento deben estar expresadas en UTC.");
        if (license.ExpiresAtUtc <= license.IssuedAtUtc)
            throw new PosLicenseDocumentException("El vencimiento debe ser posterior a la emisión.");
        if (license.IssuedAtUtc > nowUtc.AddMinutes(5))
            throw new PosLicenseDocumentException("La licencia todavía no está vigente.");
    }

    private static byte[] DecodeBase64(string value, int maxDecodedBytes, string description)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new PosLicenseDocumentException($"El {description} de la licencia no está codificado en Base64: {exception.Message}");
        }

        if (decoded.Length == 0 || decoded.Length > maxDecodedBytes)
            throw new PosLicenseDocumentException($"El {description} de la licencia está vacío o supera el tamaño permitido.");
        return decoded;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, int maxLength)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String)
            throw new PosLicenseDocumentException($"El campo '{propertyName}' debe ser texto.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new PosLicenseDocumentException($"El campo '{propertyName}' está vacío o supera el tamaño permitido.");
        return text;
    }

    private static void EnsureExactProperties(JsonElement element, string[] expected, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PosLicenseDocumentException($"El {description} debe ser un objeto JSON.");

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!actual.Add(property.Name))
                throw new PosLicenseDocumentException($"El {description} repite el campo '{property.Name}'.");

        if (!actual.SetEquals(expected))
            throw new PosLicenseDocumentException($"El {description} contiene campos desconocidos o faltantes.");
    }
}
