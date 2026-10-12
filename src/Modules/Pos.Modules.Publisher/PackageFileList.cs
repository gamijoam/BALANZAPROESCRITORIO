using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pos.Modules.Publisher;

/// <summary>Parses an explicit package allow-list; it never discovers files by scanning a directory.</summary>
public static partial class PackageFileList
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pfx", ".p12", ".pem", ".key", ".env", ".secrets", ".user", ".snk"
    };

    public static IReadOnlyList<string> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Usage("La lista de archivos debe ser un objeto JSON.");

            var rootProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!rootProperties.Add(property.Name))
                    throw Usage($"La propiedad '{property.Name}' está repetida.");
            }
            if (!rootProperties.SetEquals(["files"]))
                throw Usage("La lista debe contener únicamente la propiedad 'files'.");

            var filesElement = root.GetProperty("files");
            if (filesElement.ValueKind != JsonValueKind.Array)
                throw Usage("La propiedad 'files' debe ser una lista.");

            var result = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in filesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw Usage("Cada archivo de 'files' debe ser una ruta de texto.");
                var path = item.GetString() ?? string.Empty;
                ValidatePath(path);
                if (!unique.Add(path))
                    throw Usage($"La ruta '{path}' está repetida o solo cambia en mayúsculas/minúsculas.");
                if (IsSensitiveFileName(path))
                    throw Usage($"El archivo '{path}' tiene un nombre o extensión sensible y no puede publicarse.");
                result.Add(path);
            }

            if (result.Count == 0)
                throw Usage("La lista de archivos no puede estar vacía.");
            return result;
        }
        catch (PublisherUsageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Usage($"La lista de archivos no contiene JSON válido: {exception.Message}");
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.StartsWith('/') || path.Contains('\\') ||
            path.Contains(':') || path.EndsWith('/') || !SafePackagePathRegex().IsMatch(path))
            throw Usage($"La ruta '{path}' no es válida para el paquete.");

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    segment.EndsWith('.') || segment.EndsWith(' ') ||
                                    IsWindowsDeviceName(segment)))
            throw Usage($"La ruta '{path}' contiene un segmento inseguro.");
    }

    private static bool IsSensitiveFileName(string path)
    {
        var name = path.Split('/').Last();
        var extension = Path.GetExtension(name);
        return BlockedExtensions.Contains(extension) ||
               name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("credentials.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static PublisherUsageException Usage(string message) => new(message);

    [GeneratedRegex("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePackagePathRegex();
}
