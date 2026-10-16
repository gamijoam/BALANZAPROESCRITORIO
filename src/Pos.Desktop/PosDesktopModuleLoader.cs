using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Modules.Packaging;

namespace Pos.Desktop;

internal sealed record LoadedDesktopModule(string ModuleId, PosDesktopModuleContext Context,
    PosDesktopModuleRegistry Registry, AssemblyLoadContext LoadContext);

internal static class PosDesktopModuleLoader
{
    public static LoadedDesktopModule Load(byte[] packageBytes, DesktopModuleClientInfo descriptor,
        IPosApiClient apiClient, Guid terminalId, Func<string, CancellationToken, Task<IReadOnlyList<ProductInfo>>> searchProductsAsync,
        Func<IReadOnlyList<PosDesktopReportSourceInstance>>? getReportSources = null)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(apiClient);
        if (packageBytes.Length == 0) throw new InvalidDataException("El servidor entregó un paquete de módulo vacío.");

        using var packageStream = new MemoryStream(packageBytes, writable: false);
        var verified = ModulePackageVerifier.Verify(packageStream,
            new Dictionary<string, string>(StringComparer.Ordinal) { [descriptor.SignerKeyId] = descriptor.SignerPublicKeyPem });
        var manifest = verified.Module;
        if (!string.Equals(manifest.Id, descriptor.ModuleId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version.ToString(3), descriptor.Version, StringComparison.Ordinal) ||
            !string.Equals(verified.SignerKeyId, descriptor.SignerKeyId, StringComparison.Ordinal))
            throw new InvalidDataException("La identidad, versión o publicador del paquete no coincide con el módulo anunciado por el servidor.");
        if (manifest.DesktopEntryAssembly is null || manifest.DesktopEntryType is null || manifest.DesktopApi is null)
            throw new InvalidDataException("El paquete no declara un punto de entrada de escritorio.");
        var hostApi = new Version(1, 5, 0);
        if (!manifest.DesktopApi.Accepts(hostApi))
            throw new InvalidDataException($"El módulo requiere Desktop API {manifest.DesktopApi.Min}–{manifest.DesktopApi.MaxExclusive}; este POS ofrece {hostApi}.");

        packageStream.Position = 0;
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var assemblies = archive.Entries.Where(entry => entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => Path.GetFileName(entry.FullName), ReadEntry, StringComparer.OrdinalIgnoreCase);
        if (!assemblies.TryGetValue(manifest.DesktopEntryAssembly, out var entryBytes))
            throw new InvalidDataException($"No se encontró el ensamblado de escritorio '{manifest.DesktopEntryAssembly}'.");

        var loadContext = new DesktopModuleLoadContext(manifest.Id, assemblies);
        try
        {
            using var entryStream = new MemoryStream(entryBytes, writable: false);
            var assembly = loadContext.LoadFromStream(entryStream);
            var entryType = assembly.GetType(manifest.DesktopEntryType, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IPosDesktopModule).IsAssignableFrom(entryType) || entryType.IsAbstract ||
                !(entryType.IsPublic || entryType.IsNestedPublic) || entryType.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("El punto de entrada de escritorio debe implementar IPosDesktopModule y tener constructor público sin parámetros.");
            var module = (IPosDesktopModule)Activator.CreateInstance(entryType)!;
            if (!string.Equals(module.ModuleId, manifest.Id, StringComparison.Ordinal))
                throw new InvalidDataException("El ID del punto de entrada de escritorio no coincide con el manifiesto.");

            var context = new PosDesktopModuleContext(apiClient, terminalId,
                descriptor.GrantedPermissions.ToHashSet(StringComparer.Ordinal), manifest.Id, searchProductsAsync)
            {
                ReportApiClient = apiClient,
                GetReportSources = getReportSources
            };
            var registry = new PosDesktopModuleRegistry(manifest.Id, manifest.Permissions);
            module.Configure(registry);
            return new LoadedDesktopModule(manifest.Id, context, registry, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class DesktopModuleLoadContext(string moduleId, IReadOnlyDictionary<string, byte[]> assemblies)
        : AssemblyLoadContext($"POS desktop module {moduleId} {Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var isHostContract = string.Equals(assemblyName.Name, typeof(IPosDesktopModule).Assembly.GetName().Name, StringComparison.Ordinal) ||
                                 string.Equals(assemblyName.Name, typeof(ProductInfo).Assembly.GetName().Name, StringComparison.Ordinal);
            var shared = isHostContract ? AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.Ordinal)) : null;
            if (shared is not null) return shared;
            if (assemblyName.Name is null || !assemblies.TryGetValue(assemblyName.Name + ".dll", out var bytes)) return null;
            using var stream = new MemoryStream(bytes, writable: false);
            return LoadFromStream(stream);
        }
    }
}
