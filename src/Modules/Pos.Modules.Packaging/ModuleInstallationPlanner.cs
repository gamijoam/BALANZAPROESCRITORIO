using Pos.Modules.Abstractions;

namespace Pos.Modules.Packaging;

public sealed class ModuleInstallationPlanningException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record ModuleInstallationPlanEntry(
    string Id,
    Version Version,
    string SignerKeyId,
    string? Entitlement,
    ModuleClientApiRange ClientApi,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Migrations)
{
    public bool HasRuntimeEntry { get; init; }
}

public sealed record ModuleInstallationPlan(
    string RequestedModuleId,
    IReadOnlyList<ModuleInstallationPlanEntry> InstallationOrder)
{
    public bool IsValid => true;
}

/// <summary>Creates a read-only plan from a complete bundle of signed module packages.</summary>
public static class ModuleInstallationPlanner
{
    private const int MaxPackagesPerPlan = 32;

    public static ModuleInstallationPlan CreatePlan(
        IReadOnlyList<Stream> packageStreams,
        string requestedModuleId,
        int hostApiVersion,
        IReadOnlyDictionary<string, string> trustedPublicKeysPem,
        IReadOnlySet<string> grantedEntitlements)
    {
        ArgumentNullException.ThrowIfNull(packageStreams);
        ArgumentNullException.ThrowIfNull(trustedPublicKeysPem);
        ArgumentNullException.ThrowIfNull(grantedEntitlements);
        if (packageStreams.Count is 0 or > MaxPackagesPerPlan)
            throw PlanningFailure("package_count_invalid", $"El plan debe incluir entre 1 y {MaxPackagesPerPlan} paquetes.");
        if (string.IsNullOrWhiteSpace(requestedModuleId))
            throw PlanningFailure("module_id_invalid", "El identificador del módulo solicitado está vacío.");

        var verifiedPackages = new List<VerifiedModulePackage>(packageStreams.Count);
        foreach (var stream in packageStreams)
        {
            if (stream is null || !stream.CanRead || !stream.CanSeek)
                throw PlanningFailure("package_invalid", "Cada paquete debe ser un flujo legible y reposicionable.");
            try
            {
                verifiedPackages.Add(ModulePackageVerifier.Verify(stream, trustedPublicKeysPem));
            }
            catch (ModulePackageVerificationException exception)
            {
                throw PlanningFailure("package_invalid", exception.Message);
            }
        }

        if (verifiedPackages.Select(package => package.Module.Id).Distinct(StringComparer.Ordinal).Count() != verifiedPackages.Count)
            throw PlanningFailure("package_duplicate", "El conjunto de paquetes contiene más de una versión del mismo módulo.");

        IReadOnlyList<ModuleManifest> resolved;
        try
        {
            resolved = ModuleDependencyResolver.Resolve(
                verifiedPackages.Select(package => package.Module), [requestedModuleId], hostApiVersion);
        }
        catch (ModuleResolutionException exception)
        {
            throw PlanningFailure("dependency_invalid", exception.Message);
        }

        var verifiedById = verifiedPackages.ToDictionary(package => package.Module.Id, StringComparer.Ordinal);
        foreach (var module in resolved)
        {
            if (!string.IsNullOrWhiteSpace(module.Entitlement) && !grantedEntitlements.Contains(module.Entitlement))
                throw PlanningFailure("entitlement_unavailable", $"El módulo '{module.Id}' requiere una licencia activa para '{module.Entitlement}'.");
        }

        return new ModuleInstallationPlan(
            requestedModuleId,
            resolved.Select(module => new ModuleInstallationPlanEntry(
                module.Id,
                module.Version,
                verifiedById[module.Id].SignerKeyId,
                module.Entitlement,
                module.ClientApi!,
                module.Permissions.ToArray(),
                module.Migrations.ToArray()) { HasRuntimeEntry = module.EntryAssembly is not null }).ToArray());
    }

    private static ModuleInstallationPlanningException PlanningFailure(string code, string message) => new(code, message);
}
