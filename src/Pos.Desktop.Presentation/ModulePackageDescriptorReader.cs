using System.IO.Compression;
using System.IO;
using System.Text.Json;

namespace Pos.Desktop.Presentation;

public sealed record ModulePackageDescriptor(string Id, string Version, string DisplayName);

/// <summary>Reads non-authoritative label information for the module selection screen.
/// The server still verifies the package signature and manifest before installation.</summary>
public static class ModulePackageDescriptorReader
{
    private const int MaxManifestBytes = 1024 * 1024;

    public static ModulePackageDescriptor Read(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) throw new ArgumentException("Selecciona un archivo ZIP.", nameof(packagePath));
        using var archive = ZipFile.OpenRead(packagePath);
        var manifests = archive.Entries.Where(entry => string.Equals(entry.FullName, "module.manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifests.Length != 1)
            throw new InvalidDataException("El ZIP debe contener un único module.manifest.json en la raíz. Revisa que elegiste el ZIP del módulo, no el paquete completo de entrega.");
        if (manifests[0].Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("El manifiesto del ZIP está vacío o supera el tamaño permitido.");

        using var stream = manifests[0].Open();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var id = RequiredString(root, "id");
        var version = RequiredString(root, "version");
        var displayName = id switch
        {
            "warehouses" => "Almacenes y traslados",
            _ => id
        };
        return new ModulePackageDescriptor(id, version, displayName);
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"El manifiesto del ZIP no contiene un '{property}' válido.");
        return value.GetString()!.Trim();
    }
}
