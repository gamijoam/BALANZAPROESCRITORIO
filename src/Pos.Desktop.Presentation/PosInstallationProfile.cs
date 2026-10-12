using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Desktop.Presentation;

public enum PosInstallationMode
{
    Standalone,
    Server,
    ClientTerminal
}

/// <summary>Protected deployment settings. Client terminals deliberately cannot carry PostgreSQL credentials.</summary>
public sealed record PosInstallationProfile(
    PosInstallationMode Mode,
    string ApiBaseUrl,
    bool DatabaseConfigured = false,
    string? TerminalCredential = null,
    string? ServerCertificatePath = null,
    string? ServerCertificatePassword = null,
    string? ServerCertificateSha256 = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var apiUri))
            throw new ArgumentException("La dirección del servicio POS no es válida.", nameof(ApiBaseUrl));

        switch (Mode)
        {
            case PosInstallationMode.ClientTerminal:
                ValidateClientEndpoint(ApiBaseUrl);
                if (DatabaseConfigured)
                    throw new ArgumentException("Una terminal cliente no guarda configuración de PostgreSQL.", nameof(DatabaseConfigured));
                if (ServerCertificateSha256 is not null && !ServerCertificatePin.IsValid(ServerCertificateSha256))
                    throw new ArgumentException("La huella SHA-256 del servidor no es válida.", nameof(ServerCertificateSha256));
                if (string.IsNullOrWhiteSpace(ServerCertificateSha256) &&
                    (TerminalCredential is null || TerminalCredential.Length != 64 || TerminalCredential.Any(character => !Uri.IsHexDigit(character))))
                    throw new ArgumentException("La terminal necesita confiar en el certificado del servidor antes de conectarse.", nameof(ServerCertificateSha256));
                if (ServerCertificatePath is not null || ServerCertificatePassword is not null)
                    throw new ArgumentException("Una terminal cliente no puede guardar certificados privados del servidor.");
                break;
            case PosInstallationMode.Standalone:
                if (!apiUri.IsLoopback || apiUri.Scheme != Uri.UriSchemeHttp)
                    throw new ArgumentException("El modo independiente debe usar el servicio local por HTTP de loopback.", nameof(ApiBaseUrl));
                break;
            case PosInstallationMode.Server:
                if (apiUri.Scheme != Uri.UriSchemeHttps || apiUri.IsLoopback)
                    throw new ArgumentException("El servidor para terminales debe usar una dirección HTTPS accesible por la red privada.", nameof(ApiBaseUrl));
                if (string.IsNullOrWhiteSpace(ServerCertificatePath) || string.IsNullOrWhiteSpace(ServerCertificatePassword))
                    throw new ArgumentException("No se pudo preparar el certificado HTTPS local del servidor.", nameof(ServerCertificatePath));
                if (ServerCertificateSha256 is not null && !ServerCertificatePin.IsValid(ServerCertificateSha256))
                    throw new ArgumentException("La huella SHA-256 del servidor no es válida.", nameof(ServerCertificateSha256));
                if (TerminalCredential is not null && (TerminalCredential.Length != 64 || TerminalCredential.Any(character => !Uri.IsHexDigit(character))))
                    throw new ArgumentException("La credencial de la caja local está dañada; vuelve a emparejarla.", nameof(TerminalCredential));
                break;
        }
    }

    public static void ValidateClientEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Una terminal cliente debe usar una dirección HTTPS confiable de otra PC, sin usuario ni contraseña en la URL.", nameof(endpoint));
    }

    public static Uri CreateClientEndpoint(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Escribe la IP o el nombre de la PC servidor.", nameof(address));
        var input = address.Trim();
        var candidate = input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.IsLoopback)
            throw new ArgumentException("Escribe la IP o nombre de otra PC, por ejemplo 192.168.1.20.", nameof(address));
        if (endpoint.IsDefaultPort)
            endpoint = new UriBuilder(endpoint) { Port = 5155 }.Uri;
        ValidateClientEndpoint(endpoint.ToString());
        if (endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query))
            throw new ArgumentException("Escribe solo la IP o nombre del servidor, sin rutas ni parámetros.", nameof(address));
        return endpoint;
    }
}

/// <summary>Persists deployment settings only after encrypting them with the current Windows identity.</summary>
public sealed class PosInstallationProfileStore(string path, ILocalSecretProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public PosInstallationProfile? Load()
    {
        if (!File.Exists(path)) return null;
        byte[] plaintext;
        try { plaintext = protector.Unprotect(File.ReadAllBytes(path)); }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            throw new InvalidOperationException("No se pudo descifrar el perfil de instalación de este usuario de Windows. Vuelve a configurar este POS con la misma cuenta.", exception);
        }

        try
        {
            var profile = JsonSerializer.Deserialize<PosInstallationProfile>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("El perfil está vacío.");
            profile.Validate();
            return profile;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidOperationException("El perfil de instalación está dañado o es inválido. Vuelve a configurarlo.", exception);
        }
        finally { Array.Clear(plaintext); }
    }

    public void Save(PosInstallationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);
        byte[] protectedData;
        try { protectedData = protector.Protect(plaintext); }
        finally { Array.Clear(plaintext); }

        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("La ruta del perfil no contiene carpeta.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedData);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            Array.Clear(protectedData);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
