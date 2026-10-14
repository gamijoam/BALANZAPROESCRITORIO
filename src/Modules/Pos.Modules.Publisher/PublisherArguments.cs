using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Pos.Modules.Publisher;

public enum PublisherCommand
{
    Build,
    BuildUnsigned,
    SignPackage,
    ExportPublicKey
}

public sealed record PublisherOptions(
    PublisherCommand Command,
    string? ManifestPath,
    string? StagingDirectory,
    string? FilesListPath,
    string OutputPath,
    string? KeyId,
    string CertificateThumbprint,
    StoreLocation StoreLocation,
    string? InputPath = null,
    string? PrivateKeyPemPath = null);

public sealed class PublisherUsageException(string message) : Exception(message);

public static class PublisherArguments
{
    private static readonly Regex KeyIdPattern = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public static PublisherOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw Usage("Falta el comando. Usa 'build', 'build-unsigned', 'sign-package' o 'export-public-key'.");

        var command = args[0].ToLowerInvariant() switch
        {
            "build" => PublisherCommand.Build,
            "build-unsigned" => PublisherCommand.BuildUnsigned,
            "sign-package" => PublisherCommand.SignPackage,
            "export-public-key" => PublisherCommand.ExportPublicKey,
            _ => throw Usage($"Comando desconocido: '{args[0]}'.")
        };

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length == 2)
                throw Usage($"Se esperaba una opción, se recibió '{name}'.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw Usage($"Falta el valor de '{name}'.");
            if (!values.TryAdd(name, args[++index]))
                throw Usage($"La opción '{name}' está repetida.");
        }

        var allowed = command switch
        {
            PublisherCommand.Build => new HashSet<string>(["--manifest", "--stage", "--files", "--output", "--key-id", "--thumbprint", "--store"], StringComparer.OrdinalIgnoreCase),
            PublisherCommand.BuildUnsigned => new HashSet<string>(["--manifest", "--stage", "--files", "--output", "--key-id"], StringComparer.OrdinalIgnoreCase),
            PublisherCommand.SignPackage => new HashSet<string>(["--input", "--output", "--private-key-pem"], StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(["--output", "--thumbprint", "--store", "--private-key-pem"], StringComparer.OrdinalIgnoreCase)
        };
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null)
            throw Usage($"Opción desconocida para el comando: '{unknown}'.");

        string Required(string key)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw Usage($"Falta la opción obligatoria '{key}'.");
            return value.Trim();
        }

        var storeLocation = StoreLocation.CurrentUser;
        if (values.TryGetValue("--store", out var storeText) &&
            (!Enum.TryParse(storeText, ignoreCase: true, out storeLocation) || !Enum.IsDefined(storeLocation)))
            throw Usage("'--store' solo acepta CurrentUser o LocalMachine.");

        if (command is PublisherCommand.Build or PublisherCommand.BuildUnsigned)
        {
            var keyId = Required("--key-id");
            if (!KeyIdPattern.IsMatch(keyId))
                throw Usage("El identificador de clave firmante no es válido.");
            var thumbprint = command == PublisherCommand.Build
                ? NormalizeThumbprint(Required("--thumbprint"))
                : string.Empty;
            if (command == PublisherCommand.Build && (thumbprint.Length == 0 || thumbprint.Any(character => !Uri.IsHexDigit(character))))
                throw Usage("La huella del certificado debe contener solo dígitos hexadecimales.");

            return new PublisherOptions(
                command,
                Required("--manifest"),
                Required("--stage"),
                Required("--files"),
                Required("--output"),
                keyId,
                thumbprint,
                storeLocation);
        }

        if (command == PublisherCommand.SignPackage)
        {
            return new PublisherOptions(
                command, null, null, null,
                Required("--output"), null, string.Empty, storeLocation,
                Required("--input"), Required("--private-key-pem"));
        }

        var hasThumbprint = values.ContainsKey("--thumbprint");
        var hasPrivatePem = values.ContainsKey("--private-key-pem");
        if (hasThumbprint == hasPrivatePem)
            throw Usage("'export-public-key' requiere exactamente una de estas opciones: '--thumbprint' o '--private-key-pem'.");
        var certificateThumbprint = hasThumbprint ? NormalizeThumbprint(Required("--thumbprint")) : string.Empty;
        if (hasThumbprint && (certificateThumbprint.Length == 0 || certificateThumbprint.Any(character => !Uri.IsHexDigit(character))))
            throw Usage("La huella del certificado debe contener solo dígitos hexadecimales.");

        return new PublisherOptions(
            command, null, null, null, Required("--output"), null, certificateThumbprint, storeLocation,
            PrivateKeyPemPath: hasPrivatePem ? Required("--private-key-pem") : null);
    }

    private static string NormalizeThumbprint(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static PublisherUsageException Usage(string message) => new(message);
}
