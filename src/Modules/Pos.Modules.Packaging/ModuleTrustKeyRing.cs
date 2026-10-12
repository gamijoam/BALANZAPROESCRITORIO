using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pos.Modules.Packaging;

public sealed class ModuleTrustConfigurationException(string message) : Exception(message);

public enum ModuleSigningKeyStatus
{
    Active,
    Retiring,
    Revoked
}

/// <summary>An immutable, validated set of server-side public trust anchors.</summary>
public sealed class ModuleTrustKeyRing
{
    private const int MaxConfigurationBytes = 1024 * 1024;
    private static readonly Regex KeyIdPattern = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, string> _verifierPublicKeys;

    private ModuleTrustKeyRing(IReadOnlyDictionary<string, string> verifierPublicKeys) =>
        _verifierPublicKeys = verifierPublicKeys;

    /// <summary>Active and retiring public keys accepted for package verification.</summary>
    public IReadOnlyDictionary<string, string> VerifierPublicKeys => _verifierPublicKeys;

    public static ModuleTrustKeyRing Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ModuleTrustConfigurationException("El archivo de confianza de módulos está vacío.");

        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureExactProperties(document.RootElement, ["schemaVersion", "keys"], "archivo de claves");
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                throw new ModuleTrustConfigurationException("La versión del archivo de claves no es compatible.");

            var keysElement = document.RootElement.GetProperty("keys");
            if (keysElement.ValueKind != JsonValueKind.Array || keysElement.GetArrayLength() == 0)
                throw new ModuleTrustConfigurationException("El archivo debe incluir al menos una clave pública.");
            foreach (var keyElement in keysElement.EnumerateArray())
                EnsureExactProperties(keyElement, ["keyId", "publicKeyPem", "status"], "clave de firma");

            var configuration = JsonSerializer.Deserialize<TrustConfiguration>(json)
                ?? throw new ModuleTrustConfigurationException("No se pudo leer el archivo de claves.");
            if (configuration.Keys is null || configuration.Keys.Count == 0)
                throw new ModuleTrustConfigurationException("El archivo debe incluir al menos una clave pública.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accepted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in configuration.Keys)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.KeyId) || !KeyIdPattern.IsMatch(entry.KeyId))
                    throw new ModuleTrustConfigurationException("Una clave tiene un identificador inválido.");
                if (!seen.Add(entry.KeyId))
                    throw new ModuleTrustConfigurationException($"El identificador de clave '{entry.KeyId}' está repetido.");
                if (!Enum.TryParse<ModuleSigningKeyStatus>(entry.Status, ignoreCase: false, out var status) || !Enum.IsDefined(status))
                    throw new ModuleTrustConfigurationException($"La clave '{entry.KeyId}' tiene un estado desconocido.");

                ValidatePublicKey(entry.KeyId, entry.PublicKeyPem);
                if (status is ModuleSigningKeyStatus.Active or ModuleSigningKeyStatus.Retiring)
                    accepted.Add(entry.KeyId, entry.PublicKeyPem);
            }

            return new ModuleTrustKeyRing(new ReadOnlyDictionary<string, string>(accepted));
        }
        catch (ModuleTrustConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new ModuleTrustConfigurationException($"El archivo de claves no es válido: {exception.Message}");
        }
    }

    public static ModuleTrustKeyRing LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ModuleTrustConfigurationException("La ruta del archivo de claves está vacía.");
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var contents = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (contents.Length + read > MaxConfigurationBytes)
                    throw new ModuleTrustConfigurationException("El archivo de claves supera 1 MiB.");
                contents.Write(buffer, 0, read);
            }
            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(contents.ToArray());
            return Parse(json);
        }
        catch (ModuleTrustConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            throw new ModuleTrustConfigurationException($"No se pudo cargar el archivo de confianza: {exception.Message}");
        }
    }

    private static void ValidatePublicKey(string keyId, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) || !publicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
            throw new ModuleTrustConfigurationException($"La clave '{keyId}' debe ser pública en formato SubjectPublicKeyInfo PEM.");
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new ModuleTrustConfigurationException($"La clave pública '{keyId}' no se pudo leer.");
        }
        if (rsa.KeySize < 2048)
            throw new ModuleTrustConfigurationException($"La clave pública '{keyId}' debe tener al menos 2048 bits.");
    }

    private static void EnsureExactProperties(JsonElement element, string[] expected, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ModuleTrustConfigurationException($"La {description} debe ser un objeto JSON.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new ModuleTrustConfigurationException($"La {description} repite la propiedad '{property.Name}'.");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw new ModuleTrustConfigurationException($"La {description} contiene la propiedad no admitida '{property.Name}'.");
        }
        if (expected.Any(name => !seen.Contains(name)))
            throw new ModuleTrustConfigurationException($"La {description} no contiene todas sus propiedades obligatorias.");
    }

    private sealed record TrustConfiguration(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("keys")] IReadOnlyList<TrustKey>? Keys);

    private sealed record TrustKey(
        [property: JsonPropertyName("keyId")] string KeyId,
        [property: JsonPropertyName("publicKeyPem")] string PublicKeyPem,
        [property: JsonPropertyName("status")] string Status);
}

/// <summary>
/// Atomically replaces the active trust ring. A malformed reload clears the current ring,
/// so a future package installer must fail closed until a valid file is loaded.
/// </summary>
public sealed class ModuleTrustKeyRingStore
{
    private readonly object _sync = new();
    private ModuleTrustKeyRing? _current;

    public ModuleTrustKeyRingStore(ModuleTrustKeyRing initial) =>
        _current = initial ?? throw new ArgumentNullException(nameof(initial));

    public ModuleTrustKeyRing Current
    {
        get
        {
            lock (_sync)
                return _current ?? throw new ModuleTrustConfigurationException("No hay un anillo de confianza válido cargado.");
        }
    }

    public void Reload(string json)
    {
        lock (_sync)
        {
            try
            {
                _current = ModuleTrustKeyRing.Parse(json);
            }
            catch
            {
                _current = null;
                throw;
            }
        }
    }

    public void ReloadFromFile(string path)
    {
        lock (_sync)
        {
            try
            {
                _current = ModuleTrustKeyRing.LoadFromFile(path);
            }
            catch
            {
                _current = null;
                throw;
            }
        }
    }
}
