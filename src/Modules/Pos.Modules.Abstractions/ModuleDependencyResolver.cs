namespace Pos.Modules.Abstractions;

public sealed class ModuleResolutionException(string message) : Exception(message);

/// <summary>Resolves requested modules and their dependencies without performing side effects.</summary>
public static class ModuleDependencyResolver
{
    public static IReadOnlyList<ModuleManifest> Resolve(
        IEnumerable<ModuleManifest> availableModules,
        IEnumerable<string> requestedModuleIds,
        int hostApiVersion)
    {
        ArgumentNullException.ThrowIfNull(availableModules);
        ArgumentNullException.ThrowIfNull(requestedModuleIds);
        if (hostApiVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(hostApiVersion), "La versión del host API debe ser positiva.");

        var catalog = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
        foreach (var module in availableModules)
        {
            try
            {
                ModuleManifestReader.Validate(module);
            }
            catch (Exception exception) when (exception is ModuleManifestException or ArgumentNullException)
            {
                throw new ModuleResolutionException($"Hay un manifiesto inválido: {exception.Message}");
            }

            if (!catalog.TryAdd(module.Id, module))
                throw new ModuleResolutionException($"El catálogo contiene más de un módulo con id '{module.Id}'.");
        }

        var requested = requestedModuleIds.ToArray();
        if (requested.Length == 0)
            return [];
        if (requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new ModuleResolutionException("La selección contiene un id vacío o repetido.");

        var ordered = new List<ModuleManifest>();
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);

        foreach (var id in requested.Order(StringComparer.Ordinal))
            Visit(id, requiredBy: null);

        return ordered;

        void Visit(string id, string? requiredBy)
        {
            if (!catalog.TryGetValue(id, out var module))
                throw new ModuleResolutionException(requiredBy is null
                    ? $"El módulo solicitado '{id}' no está disponible."
                    : $"El módulo '{requiredBy}' requiere '{id}', pero no está disponible.");

            if (states.TryGetValue(id, out var state))
            {
                if (state == VisitState.Visiting)
                    throw new ModuleResolutionException($"Se detectó una dependencia circular que incluye '{id}'.");
                return;
            }

            if (hostApiVersion < module.HostApi.Min || hostApiVersion > module.HostApi.Max)
                throw new ModuleResolutionException(
                    $"El módulo '{id}' requiere host API {module.HostApi.Min}–{module.HostApi.Max}; el anfitrión usa {hostApiVersion}.");

            states[id] = VisitState.Visiting;
            foreach (var dependency in module.Dependencies.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                if (!catalog.TryGetValue(dependency.Id, out var dependencyModule))
                    throw new ModuleResolutionException($"El módulo '{id}' requiere '{dependency.Id}', pero no está disponible.");
                if (!dependency.Accepts(dependencyModule.Version))
                    throw new ModuleResolutionException(
                        $"El módulo '{id}' requiere '{dependency.Id}' entre {dependency.MinVersion} (incluida) y {dependency.MaxVersionExclusive} (excluida); hay {dependencyModule.Version}.");
                Visit(dependency.Id, id);
            }

            states[id] = VisitState.Visited;
            ordered.Add(module);
        }
    }

    private enum VisitState { Visiting, Visited }
}
