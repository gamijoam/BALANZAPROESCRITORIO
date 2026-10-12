using System.Security.Cryptography.X509Certificates;

namespace Pos.Modules.Publisher;

public enum PublisherCommand
{
    Build,
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
    StoreLocation StoreLocation);

public sealed class PublisherUsageException(string message) : Exception(message);

public static class PublisherArguments
{
    public static PublisherOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw Usage("Falta el comando. Usa 'build' o 'export-public-key'.");

        var command = args[0].ToLowerInvariant() switch
        {
            "build" => PublisherCommand.Build,
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

        var allowed = command == PublisherCommand.Build
            ? new HashSet<string>(["--manifest", "--stage", "--files", "--output", "--key-id", "--thumbprint", "--store"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["--output", "--thumbprint", "--store"], StringComparer.OrdinalIgnoreCase);
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null)
            throw Usage($"Opción desconocida para el comando: '{unknown}'.");

        string Required(string key)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw Usage($"Falta la opción obligatoria '{key}'.");
            return value.Trim();
        }

        var thumbprint = NormalizeThumbprint(Required("--thumbprint"));
        if (thumbprint.Length == 0 || thumbprint.Any(character => !Uri.IsHexDigit(character)))
            throw Usage("La huella del certificado debe contener solo dígitos hexadecimales.");

        var storeLocation = StoreLocation.CurrentUser;
        if (values.TryGetValue("--store", out var storeText) &&
            (!Enum.TryParse(storeText, ignoreCase: true, out storeLocation) || !Enum.IsDefined(storeLocation)))
            throw Usage("'--store' solo acepta CurrentUser o LocalMachine.");

        if (command == PublisherCommand.Build)
        {
            return new PublisherOptions(
                command,
                Required("--manifest"),
                Required("--stage"),
                Required("--files"),
                Required("--output"),
                Required("--key-id"),
                thumbprint,
                storeLocation);
        }

        return new PublisherOptions(
            command,
            null,
            null,
            null,
            Required("--output"),
            null,
            thumbprint,
            storeLocation);
    }

    private static string NormalizeThumbprint(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static PublisherUsageException Usage(string message) => new(message);
}
