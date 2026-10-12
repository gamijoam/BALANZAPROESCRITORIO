using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Routing.Patterns;
using Pos.Modules.Abstractions;
using Pos.Modules.Packaging;

namespace Pos.Infrastructure.Modules;

public sealed class ModuleRuntimeLoadException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class LoadedPosModule(
    VerifiedModulePackage package,
    IPosServerModule instance,
    IReadOnlyList<PosModuleEndpoint> endpoints,
    AssemblyLoadContext loadContext)
{
    public VerifiedModulePackage Package { get; } = package;
    public IPosServerModule Instance { get; } = instance;
    public IReadOnlyList<PosModuleEndpoint> Endpoints { get; } = endpoints;
    internal AssemblyLoadContext LoadContext { get; } = loadContext;
}

/// <summary>Loads entry code only after signature verification. Assemblies stay in memory and are never extracted to disk.</summary>
public static class ModuleRuntimeLoader
{
    public static LoadedPosModule Load(Stream packageStream, ModuleTrustKeyRing trustKeyRing)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        ArgumentNullException.ThrowIfNull(trustKeyRing);
        var verified = ModulePackageVerifier.Verify(packageStream, trustKeyRing);
        if (verified.Module.EntryAssembly is null || verified.Module.EntryType is null)
            throw new ModuleRuntimeLoadException("module_entry_missing", $"El paquete '{verified.Module.Id}' no declara un punto de entrada ejecutable.");

        var originalPosition = packageStream.Position;
        try
        {
            packageStream.Position = 0;
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            var assemblies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(entry.FullName);
                if (!assemblies.TryAdd(name, ReadEntry(entry)))
                    throw new ModuleRuntimeLoadException("module_assembly_name_conflict", $"El paquete contiene ensamblados duplicados llamados '{name}'.");
            }
            if (!assemblies.TryGetValue(verified.Module.EntryAssembly, out var entryAssemblyBytes))
                throw new ModuleRuntimeLoadException("module_entry_assembly_missing", $"No se encontró '{verified.Module.EntryAssembly}' dentro del paquete firmado.");

            var loadContext = new VerifiedModuleLoadContext(verified.Module.Id, assemblies);
            try
            {
                using var entryStream = new MemoryStream(entryAssemblyBytes, writable: false);
                var assembly = loadContext.LoadFromStream(entryStream);
                var type = assembly.GetType(verified.Module.EntryType, throwOnError: true, ignoreCase: false)!;
                if (!typeof(IPosServerModule).IsAssignableFrom(type) || type.IsAbstract ||
                    !(type.IsPublic || type.IsNestedPublic) || type.GetConstructor(Type.EmptyTypes) is null)
                    throw new ModuleRuntimeLoadException("module_entry_type_invalid", "El punto de entrada debe implementar IPosServerModule y tener constructor público sin parámetros.");
                var instance = (IPosServerModule)Activator.CreateInstance(type)!;
                if (!string.Equals(instance.ModuleId, verified.Module.Id, StringComparison.Ordinal))
                    throw new ModuleRuntimeLoadException("module_entry_id_mismatch", "El id expuesto por el punto de entrada no coincide con el manifiesto.");
                var endpoints = instance.GetEndpoints() ?? throw new ModuleRuntimeLoadException("module_endpoints_missing", "El módulo devolvió una lista de rutas vacía o inválida.");
                ValidateEndpoints(endpoints);
                return new LoadedPosModule(verified, instance, endpoints.ToArray(), loadContext);
            }
            catch
            {
                loadContext.Unload();
                throw;
            }
        }
        catch (ModuleRuntimeLoadException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModuleRuntimeLoadException("module_entry_load_failed", $"No se pudo cargar el módulo firmado '{verified.Module.Id}': {exception.GetBaseException().Message}", exception);
        }
        finally
        {
            packageStream.Position = originalPosition;
        }
    }

    private static void ValidateEndpoints(IReadOnlyList<PosModuleEndpoint> endpoints)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null || endpoint.Handler is null || endpoint.Method is null || endpoint.Route is null)
                throw new ModuleRuntimeLoadException("module_endpoint_invalid", "El módulo declaró una ruta incompleta.");
            var method = endpoint.Method.ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE") ||
                endpoint.Route.Length == 0 || endpoint.Route.StartsWith('/') || endpoint.Route.Contains('\\') ||
                endpoint.Route.Contains("..", StringComparison.Ordinal) || endpoint.Route.Contains('?') || endpoint.Route.Contains('#'))
                throw new ModuleRuntimeLoadException("module_endpoint_invalid", "Los módulos solo pueden declarar métodos HTTP admitidos y rutas relativas seguras.");
            try { RoutePatternFactory.Parse(endpoint.Route); }
            catch (RoutePatternException exception)
            { throw new ModuleRuntimeLoadException("module_endpoint_invalid", $"La ruta '{endpoint.Route}' no es válida: {exception.Message}", exception); }
            if (!keys.Add(method + " " + endpoint.Route))
                throw new ModuleRuntimeLoadException("module_endpoint_duplicate", $"La ruta '{method} {endpoint.Route}' está duplicada.");
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var target = new MemoryStream(capacity: checked((int)entry.Length));
        source.CopyTo(target);
        return target.ToArray();
    }

    private sealed class VerifiedModuleLoadContext(string moduleId, IReadOnlyDictionary<string, byte[]> assemblies)
        : AssemblyLoadContext($"POS module {moduleId} {Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sharedAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
            if (sharedAssembly is not null) return sharedAssembly;
            if (assemblyName.Name is null || !assemblies.TryGetValue(assemblyName.Name + ".dll", out var bytes)) return null;
            using var stream = new MemoryStream(bytes, writable: false);
            return LoadFromStream(stream);
        }
    }
}
