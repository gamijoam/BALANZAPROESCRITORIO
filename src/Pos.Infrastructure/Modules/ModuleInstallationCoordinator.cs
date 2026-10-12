using Pos.Modules.Packaging;

namespace Pos.Infrastructure.Modules;

public sealed record ModuleInstallationPackage(string ModuleId, Version Version, Stream Content);

public sealed record ModuleInstallationRequest(
    ModuleInstallationPlan Plan,
    IReadOnlyList<ModuleInstallationPackage> Packages,
    string ConnectionString,
    string PostgresBinaryDirectory,
    string BackupDirectory,
    string ModuleStorageDirectory,
    int HostApiVersion,
    ModuleTrustKeyRing TrustKeyRing,
    IReadOnlySet<string> GrantedEntitlements);

public sealed record ModuleInstallationResult(
    string RequestedModuleId,
    string Status,
    ModuleInstallationPlan Plan,
    VerifiedModuleDatabaseBackup Backup,
    IReadOnlyList<StoredModulePackage> Packages,
    IReadOnlyList<ModuleMigrationExecutionResult> Migrations,
    bool RestartRequired);

public interface IModuleInstallationBackup
{
    Task<VerifiedModuleDatabaseBackup> CreateAsync(ModuleInstallationRequest request, CancellationToken cancellationToken);
}

public interface IModuleInstallationPackageStore
{
    Task<StoredModulePackage> StoreAsync(ModuleInstallationPackage package, string storageRoot, ModuleTrustKeyRing trustKeyRing, CancellationToken cancellationToken);
}

public interface IModuleInstallationMigrations
{
    Task<IReadOnlyList<ModuleMigrationExecutionResult>> ApplyAsync(ModuleInstallationRequest request, IReadOnlyList<StoredModulePackage> packages, CancellationToken cancellationToken);
}

/// <summary>Coordinates a safe, restart-pending install. Stored assemblies remain inert until the host restarts and loads them.</summary>
public sealed class ModuleInstallationCoordinator(
    IModuleInstallationBackup backup,
    IModuleInstallationPackageStore packageStore,
    IModuleInstallationMigrations migrations)
{
    public async Task<ModuleInstallationResult> InstallAsync(ModuleInstallationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentNullException.ThrowIfNull(request.TrustKeyRing);
        ArgumentNullException.ThrowIfNull(request.GrantedEntitlements);
        ArgumentNullException.ThrowIfNull(request.Packages);
        if (request.Plan.InstallationOrder.Count == 0 || request.Packages.Count != request.Plan.InstallationOrder.Count)
            throw new InvalidOperationException("El paquete no coincide con el plan de instalación aprobado.");
        for (var index = 0; index < request.Packages.Count; index++)
        {
            var planned = request.Plan.InstallationOrder[index];
            var package = request.Packages[index];
            if (!string.Equals(planned.Id, package.ModuleId, StringComparison.Ordinal) || planned.Version != package.Version ||
                package.Content is null || !package.Content.CanRead || !package.Content.CanSeek)
                throw new InvalidOperationException("Los paquetes deben coincidir exactamente con el plan y el orden de dependencias.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var verifiedBackup = await backup.CreateAsync(request, cancellationToken);

        var storedPackages = new List<StoredModulePackage>(request.Packages.Count);
        foreach (var package in request.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            package.Content.Position = 0;
            var stored = await packageStore.StoreAsync(package, request.ModuleStorageDirectory, request.TrustKeyRing, cancellationToken);
            if (!string.Equals(stored.ModuleId, package.ModuleId, StringComparison.Ordinal) || stored.Version != package.Version)
                throw new InvalidOperationException("El contenido firmado no coincide con el módulo o versión del plan.");
            storedPackages.Add(stored);
        }

        var applied = await migrations.ApplyAsync(request, storedPackages, cancellationToken);
        return new ModuleInstallationResult(request.Plan.RequestedModuleId, "RestartPending", request.Plan, verifiedBackup,
            storedPackages, applied, RestartRequired: true);
    }
}

public sealed class ModuleInstallationBackupAdapter(ModuleDatabaseBackupService service) : IModuleInstallationBackup
{
    public Task<VerifiedModuleDatabaseBackup> CreateAsync(ModuleInstallationRequest request, CancellationToken cancellationToken) =>
        service.CreateVerifiedBackupAsync(request.ConnectionString, request.PostgresBinaryDirectory,
            request.BackupDirectory, request.ModuleStorageDirectory, cancellationToken);
}

public sealed class ModuleInstallationPackageStoreAdapter : IModuleInstallationPackageStore
{
    public Task<StoredModulePackage> StoreAsync(ModuleInstallationPackage package, string storageRoot,
        ModuleTrustKeyRing trustKeyRing, CancellationToken cancellationToken) =>
        ModulePackageStore.StoreAsync(package.Content, storageRoot, trustKeyRing, cancellationToken);
}

public sealed class ModuleInstallationMigrationAdapter(ModuleMigrationExecutor executor) : IModuleInstallationMigrations
{
    public Task<IReadOnlyList<ModuleMigrationExecutionResult>> ApplyAsync(ModuleInstallationRequest request,
        IReadOnlyList<StoredModulePackage> packages, CancellationToken cancellationToken) =>
        executor.ApplyAsync(request.ConnectionString, packages.Select(package => package.PackagePath).ToArray(),
            request.Plan.RequestedModuleId, request.HostApiVersion, request.TrustKeyRing, request.GrantedEntitlements, cancellationToken);
}
