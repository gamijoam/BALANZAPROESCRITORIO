using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pos.Modules.Abstractions;

public static partial class ModuleManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static ModuleManifest ParseAndValidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ModuleManifestException("El manifiesto está vacío.");

        ModuleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModuleManifest>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ModuleManifestException($"El manifiesto JSON no es válido: {exception.Message}");
        }

        if (manifest is null)
            throw new ModuleManifestException("El manifiesto no contiene un módulo.");

        Validate(manifest);
        return manifest;
    }

    public static void Validate(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        RequireMatch(manifest.Id, ModuleIdPattern(), "El id del módulo debe usar minúsculas, números y guiones.");
        ValidateVersion(manifest.Version, "versión del módulo");

        if (manifest.HostApi is null || manifest.HostApi.Min < 1 || manifest.HostApi.Max < manifest.HostApi.Min)
            throw new ModuleManifestException("El rango hostApi debe tener versiones enteras positivas y min <= max.");

        if (manifest.ClientApi is null)
            throw new ModuleManifestException("El manifiesto debe declarar clientApi para comprobar todas las terminales registradas.");
        ValidateVersion(manifest.ClientApi.Min, "versión mínima de clientApi");
        ValidateVersion(manifest.ClientApi.MaxExclusive, "versión máxima exclusiva de clientApi");
        if (manifest.ClientApi.Min >= manifest.ClientApi.MaxExclusive)
            throw new ModuleManifestException("El rango clientApi debe tener min < maxExclusive.");

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) != string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new ModuleManifestException("entryAssembly y entryType deben declararse juntos.");
        if (manifest.EntryAssembly is not null && !EntryAssemblyPattern().IsMatch(manifest.EntryAssembly))
            throw new ModuleManifestException("entryAssembly debe ser un nombre simple terminado en .dll, sin rutas.");
        if (manifest.EntryType is not null && !EntryTypePattern().IsMatch(manifest.EntryType))
            throw new ModuleManifestException("entryType debe ser el nombre completo de un tipo CLR válido.");

        var hasDesktopEntry = manifest.DesktopEntryAssembly is not null || manifest.DesktopEntryType is not null;
        if (hasDesktopEntry && (manifest.DesktopEntryAssembly is null || manifest.DesktopEntryType is null || manifest.DesktopApi is null))
            throw new ModuleManifestException("desktopEntryAssembly, desktopEntryType y desktopApi deben declararse juntos.");
        if (!hasDesktopEntry && manifest.DesktopApi is not null)
            throw new ModuleManifestException("desktopApi solo se declara cuando el módulo contiene un punto de entrada de escritorio.");
        if (manifest.DesktopEntryAssembly is not null && !EntryAssemblyPattern().IsMatch(manifest.DesktopEntryAssembly))
            throw new ModuleManifestException("desktopEntryAssembly debe ser un nombre simple terminado en .dll, sin rutas.");
        if (manifest.DesktopEntryType is not null && !EntryTypePattern().IsMatch(manifest.DesktopEntryType))
            throw new ModuleManifestException("desktopEntryType debe ser el nombre completo de un tipo CLR válido.");
        if (manifest.DesktopApi is not null)
        {
            ValidateVersion(manifest.DesktopApi.Min, "versión mínima de desktopApi");
            ValidateVersion(manifest.DesktopApi.MaxExclusive, "versión máxima exclusiva de desktopApi");
            if (manifest.DesktopApi.Min >= manifest.DesktopApi.MaxExclusive)
                throw new ModuleManifestException("El rango desktopApi debe tener min < maxExclusive.");
        }

        if (manifest.Dependencies is null || manifest.Permissions is null || manifest.Migrations is null)
            throw new ModuleManifestException("dependencies, permissions y migrations deben estar presentes como listas.");

        EnsureUnique(manifest.Dependencies.Select(item => item?.Id), "dependencias");
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency is null)
                throw new ModuleManifestException("La lista de dependencias contiene un elemento nulo.");
            RequireMatch(dependency.Id, ModuleIdPattern(), "El id de una dependencia no es válido.");
            if (dependency.Id == manifest.Id)
                throw new ModuleManifestException($"El módulo '{manifest.Id}' no puede depender de sí mismo.");
            ValidateVersion(dependency.MinVersion, $"versión mínima de '{dependency.Id}'");
            ValidateVersion(dependency.MaxVersionExclusive, $"versión máxima de '{dependency.Id}'");
            if (dependency.MinVersion >= dependency.MaxVersionExclusive)
                throw new ModuleManifestException($"El rango de versiones de '{dependency.Id}' está vacío o invertido.");
        }

        EnsureUnique(manifest.Permissions, "permisos");
        foreach (var permission in manifest.Permissions)
            RequireMatch(permission, PermissionIdPattern(), "Un permiso debe tener formato Nombre.Accion.");

        if (manifest.Entitlement is not null)
            RequireMatch(manifest.Entitlement, EntitlementPattern(), "El entitlement no tiene un formato válido.");

        EnsureUnique(manifest.Migrations, "migraciones");
        foreach (var migration in manifest.Migrations)
            RequireMatch(migration, MigrationIdPattern(), "El id de una migración no tiene un formato válido.");
    }

    private static void ValidateVersion(Version? version, string description)
    {
        if (version is null || version.Major < 0 || version.Minor < 0 || version.Build < 0 || version.Revision != -1)
            throw new ModuleManifestException($"La {description} debe usar el formato SemVer estable major.minor.patch.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new StableVersionJsonConverter());
        return options;
    }

    private static void EnsureUnique(IEnumerable<string?>? values, string description)
    {
        if (values is null)
            throw new ModuleManifestException($"La lista de {description} no está definida.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                throw new ModuleManifestException($"La lista de {description} contiene un valor vacío o repetido.");
        }
    }

    private static void RequireMatch(string? value, Regex pattern, string message)
    {
        if (value is null || !pattern.IsMatch(value))
            throw new ModuleManifestException(message);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$")]
    private static partial Regex ModuleIdPattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$")]
    private static partial Regex PermissionIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$")]
    private static partial Regex EntitlementPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*:[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex MigrationIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex EntryAssemblyPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)+$")]
    private static partial Regex EntryTypePattern();

    private sealed class StableVersionJsonConverter : JsonConverter<Version>
    {
        private static readonly Regex Pattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);

        public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !Pattern.IsMatch(value) || !Version.TryParse(value, out var version))
                throw new JsonException("Las versiones deben usar SemVer estable major.minor.patch, sin ceros iniciales.");
            return version;
        }

        public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(3));
    }
}
