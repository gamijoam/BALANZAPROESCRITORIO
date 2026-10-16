using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Domain.Licensing;

namespace Pos.Application.Licensing;

/// <summary>Verifies signed control commands that apply to one offline POS installation.</summary>
public sealed class PosLicenseControlDocumentVerifier
{
    private const int MaxDocumentBytes = 32 * 1024;
    private const int MaxPayloadBytes = 16 * 1024;
    private static readonly byte[] SignatureContext = Encoding.UTF8.GetBytes("BalanzaPro.POS-CONTROL.v1\n");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private readonly IReadOnlyDictionary<string, string> _trustedPublicKeys;

    public PosLicenseControlDocumentVerifier(IReadOnlyDictionary<string, string> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        if (trustedPublicKeys.Count == 0)
            throw new ArgumentException("Debe configurarse al menos una llave pública de licencias.", nameof(trustedPublicKeys));
        _trustedPublicKeys = trustedPublicKeys;
    }

    public PosLicenseControlDocument Verify(ReadOnlySpan<byte> documentBytes, Guid installationId, string licenseId,
        long currentControlVersion, DateTimeOffset nowUtc)
    {
        if (documentBytes.IsEmpty || documentBytes.Length > MaxDocumentBytes)
            throw new PosLicenseDocumentException("La orden de control está vacía o supera el tamaño permitido.");

        try
        {
            using var envelopeDocument = JsonDocument.Parse(documentBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var envelope = envelopeDocument.RootElement;
            EnsureExactProperties(envelope, ["schemaVersion", "keyId", "payload", "signature"], "sobre de control");
            if (envelope.GetProperty("schemaVersion").GetInt32() != 1)
                throw new PosLicenseDocumentException("La versión de la orden de control no es compatible.");

            var keyId = ReadRequiredString(envelope, "keyId", 64);
            if (!_trustedPublicKeys.TryGetValue(keyId, out var publicKeyPem))
                throw new PosLicenseDocumentException("La orden de control fue firmada con una llave no confiable.");

            var payloadBytes = DecodeBase64(ReadRequiredString(envelope, "payload", MaxPayloadBytes * 2), MaxPayloadBytes);
            var signature = DecodeBase64(ReadRequiredString(envelope, "signature", 16 * 1024), 8 * 1024);
            var signedBytes = BuildSignedBytes(keyId, payloadBytes);
            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(publicKeyPem);
                if (!rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new PosLicenseDocumentException("La firma de la orden de control no es válida.");
            }

            using var payloadDocument = JsonDocument.Parse(payloadBytes, new JsonDocumentOptions { MaxDepth = 8 });
            EnsureExactProperties(payloadDocument.RootElement,
                ["schemaVersion", "licenseId", "installationId", "controlVersion", "status", "reason", "issuedAtUtc"],
                "contenido de control");
            var control = JsonSerializer.Deserialize<PosLicenseControlDocument>(payloadBytes, JsonOptions)
                ?? throw new PosLicenseDocumentException("No se pudo leer la orden de control.");
            if (control.SchemaVersion != 1 || control.LicenseId != licenseId || control.InstallationId != installationId)
                throw new PosLicenseDocumentException("La orden de control pertenece a otra licencia o instalación.");
            if (control.ControlVersion <= currentControlVersion || control.ControlVersion <= 0)
                throw new PosLicenseDocumentException("La orden de control ya fue aplicada o está desactualizada.");
            if (!string.Equals(control.Status, "Suspended", StringComparison.Ordinal))
                throw new PosLicenseDocumentException("La orden de control contiene un estado no admitido.");
            if (control.Reason?.Length > 512)
                throw new PosLicenseDocumentException("El motivo de suspensión es demasiado largo.");
            if (control.IssuedAtUtc.Offset != TimeSpan.Zero || control.IssuedAtUtc > nowUtc.AddMinutes(5))
                throw new PosLicenseDocumentException("La fecha de la orden de control no es válida.");
            return control;
        }
        catch (PosLicenseDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new PosLicenseDocumentException($"La orden de control no es válida: {exception.Message}");
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

    private static byte[] DecodeBase64(string value, int maxDecodedBytes)
    {
        var decoded = Convert.FromBase64String(value);
        if (decoded.Length == 0 || decoded.Length > maxDecodedBytes)
            throw new PosLicenseDocumentException("La orden de control contiene un bloque demasiado grande.");
        return decoded;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, int maxLength)
    {
        var value = element.GetProperty(propertyName);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new PosLicenseDocumentException($"El campo '{propertyName}' de control no es válido.");
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
