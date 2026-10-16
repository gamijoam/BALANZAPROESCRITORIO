using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Domain.Licensing;

namespace Pos.Application.Licensing;

/// <summary>Verifies a time-limited recovery grant against the exact local request.</summary>
public sealed class PosLicenseRecoveryGrantVerifier
{
    private const int MaxDocumentBytes = 32 * 1024;
    private const int MaxPayloadBytes = 16 * 1024;
    private static readonly byte[] SignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-RECOVERY.v1\n");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private readonly IReadOnlyDictionary<string, string> _trustedPublicKeys;

    public PosLicenseRecoveryGrantVerifier(IReadOnlyDictionary<string, string> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        if (trustedPublicKeys.Count == 0)
            throw new ArgumentException("Debe configurarse al menos una llave pública de licencias.", nameof(trustedPublicKeys));
        _trustedPublicKeys = trustedPublicKeys;
    }

    public PosLicenseRecoveryGrant Verify(ReadOnlySpan<byte> documentBytes, PosLicenseRecoveryRequest request,
        DateTimeOffset nowUtc, bool requireCurrent = true)
    {
        if (documentBytes.IsEmpty || documentBytes.Length > MaxDocumentBytes)
            throw new PosLicenseDocumentException("El archivo de desbloqueo está vacío o supera el tamaño permitido.");
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("La hora actual debe estar expresada en UTC.", nameof(nowUtc));

        try
        {
            using var envelopeDocument = JsonDocument.Parse(documentBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var envelope = envelopeDocument.RootElement;
            EnsureExactProperties(envelope, ["schemaVersion", "keyId", "payload", "signature"], "sobre de desbloqueo");
            if (envelope.GetProperty("schemaVersion").GetInt32() != 1)
                throw new PosLicenseDocumentException("La versión del archivo de desbloqueo no es compatible.");
            var keyId = ReadRequiredString(envelope, "keyId", 64);
            if (!_trustedPublicKeys.TryGetValue(keyId, out var publicKeyPem))
                throw new PosLicenseDocumentException("El desbloqueo fue firmado con una llave no confiable.");

            var payloadBytes = DecodeBase64(ReadRequiredString(envelope, "payload", MaxPayloadBytes * 2), MaxPayloadBytes);
            var signature = DecodeBase64(ReadRequiredString(envelope, "signature", 16 * 1024), 8 * 1024);
            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(publicKeyPem);
                if (!rsa.VerifyData(BuildSignedBytes(keyId, payloadBytes), signature,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new PosLicenseDocumentException("La firma del archivo de desbloqueo no es válida.");
            }

            using var payloadDocument = JsonDocument.Parse(payloadBytes, new JsonDocumentOptions { MaxDepth = 8 });
            EnsureExactProperties(payloadDocument.RootElement,
                ["schemaVersion", "licenseId", "installationId", "requestId", "nonce", "issuedAtUtc", "expiresAtUtc", "reason"],
                "contenido del desbloqueo");
            var grant = JsonSerializer.Deserialize<PosLicenseRecoveryGrant>(payloadBytes, JsonOptions)
                ?? throw new PosLicenseDocumentException("No se pudo leer el archivo de desbloqueo.");
            if (grant.SchemaVersion != 1 || grant.LicenseId != request.LicenseId || grant.InstallationId != request.InstallationId ||
                grant.RequestId != request.RequestId || !FixedTimeNonceEquals(grant.Nonce, request.Nonce))
                throw new PosLicenseDocumentException("El desbloqueo no corresponde a la solicitud pendiente de esta instalación.");
            if (grant.IssuedAtUtc.Offset != TimeSpan.Zero || grant.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                grant.IssuedAtUtc > nowUtc.AddMinutes(5) || (requireCurrent && grant.ExpiresAtUtc <= nowUtc) || grant.ExpiresAtUtc <= grant.IssuedAtUtc ||
                grant.ExpiresAtUtc > grant.IssuedAtUtc.AddHours(48))
                throw new PosLicenseDocumentException("El desbloqueo venció o supera las 48 horas máximas permitidas.");
            if (string.IsNullOrWhiteSpace(grant.Reason) || grant.Reason.Length > 512)
                throw new PosLicenseDocumentException("El motivo del desbloqueo no es válido.");
            return grant;
        }
        catch (PosLicenseDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new PosLicenseDocumentException($"El archivo de desbloqueo no es válido: {exception.Message}");
        }
    }

    public static byte[] BuildSignedBytes(string keyId, ReadOnlySpan<byte> payloadBytes)
    {
        var keyBytes = Encoding.UTF8.GetBytes(keyId + "\n");
        var signedBytes = new byte[SignatureContext.Length + keyBytes.Length + payloadBytes.Length];
        SignatureContext.CopyTo(signedBytes, 0);
        keyBytes.CopyTo(signedBytes, SignatureContext.Length);
        payloadBytes.CopyTo(signedBytes.AsSpan(SignatureContext.Length + keyBytes.Length));
        return signedBytes;
    }

    private static bool FixedTimeNonceEquals(string provided, string expected)
    {
        try
        {
            var providedBytes = Convert.FromBase64String(provided);
            var expectedBytes = Convert.FromBase64String(expected);
            return providedBytes.Length == 32 && expectedBytes.Length == 32 &&
                   CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] DecodeBase64(string value, int maximumBytes)
    {
        var decoded = Convert.FromBase64String(value);
        if (decoded.Length is 0 || decoded.Length > maximumBytes)
            throw new PosLicenseDocumentException("El archivo de desbloqueo contiene un bloque demasiado grande.");
        return decoded;
    }

    private static string ReadRequiredString(JsonElement element, string name, int maximumLength)
    {
        var value = element.GetProperty(name);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength)
            throw new PosLicenseDocumentException($"El campo '{name}' del desbloqueo no es válido.");
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
