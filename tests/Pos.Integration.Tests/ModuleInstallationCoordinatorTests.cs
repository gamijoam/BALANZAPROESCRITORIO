using Pos.Infrastructure.Modules;
using Pos.Modules.Packaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pos.Integration.Tests;

public sealed class ModuleInstallationCoordinatorTests
{
    [Fact]
    public async Task Install_creates_backup_before_storing_packages_and_applying_migrations()
    {
        var calls = new List<string>();
        var coordinator = new ModuleInstallationCoordinator(
            new FakeBackup(calls), new FakePackageStore(calls), new FakeMigrations(calls));
        var request = Request();

        var result = await coordinator.InstallAsync(request);

        Assert.Equal(new[] { "backup", "store:inventory", "migrate" }, calls);
        Assert.Equal("backup.dump", result.Backup.BackupPath);
        Assert.True(result.RestartRequired);
        Assert.Equal("RestartPending", result.Status);
    }

    [Fact]
    public async Task Install_stops_before_storage_when_verified_backup_fails()
    {
        var calls = new List<string>();
        var coordinator = new ModuleInstallationCoordinator(
            new FakeBackup(calls, fail: true), new FakePackageStore(calls), new FakeMigrations(calls));

        await Assert.ThrowsAsync<ModuleDatabaseBackupException>(() => coordinator.InstallAsync(Request()));

        Assert.Equal(new[] { "backup" }, calls);
    }

    [Fact]
    public async Task Install_keeps_dependency_order_and_never_migrates_when_package_storage_fails()
    {
        var calls = new List<string>();
        var request = Request() with
        {
            Plan = new ModuleInstallationPlan("inventory", [
                new ModuleInstallationPlanEntry("core-data", new Version(1, 0, 0), "official", null, new(new Version(1, 0, 0), new Version(2, 0, 0)), [], []),
                new ModuleInstallationPlanEntry("inventory", new Version(1, 0, 0), "official", null, new(new Version(1, 0, 0), new Version(2, 0, 0)), [], [])]),
            Packages = [
                new ModuleInstallationPackage("core-data", new Version(1, 0, 0), new MemoryStream([1])),
                new ModuleInstallationPackage("inventory", new Version(1, 0, 0), new MemoryStream([2]))]
        };
        var coordinator = new ModuleInstallationCoordinator(new FakeBackup(calls), new FailingPackageStore(calls), new FakeMigrations(calls));

        await Assert.ThrowsAsync<ModulePackageStoreException>(() => coordinator.InstallAsync(request));

        Assert.Equal(new[] { "backup", "store:core-data" }, calls);
    }

    private static ModuleInstallationRequest Request() => new(
        new ModuleInstallationPlan("inventory", [new ModuleInstallationPlanEntry("inventory", new Version(1, 0, 0), "official", null, new(new Version(1, 0, 0), new Version(2, 0, 0)), [], [])]),
        [new ModuleInstallationPackage("inventory", new Version(1, 0, 0), new MemoryStream([1, 2, 3]))],
        "Host=localhost;Database=pos;Username=pos;Password=secret", "C:\\pg", "C:\\backups", "C:\\modules", 1,
        TrustRing(), new HashSet<string>());

    private static ModuleTrustKeyRing TrustRing()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        return ModuleTrustKeyRing.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            keys = new[] { new { keyId = "official", publicKeyPem = publicKey, status = "Active" } }
        }));
    }

    private sealed class FakeBackup(List<string> calls, bool fail = false) : IModuleInstallationBackup
    {
        public Task<VerifiedModuleDatabaseBackup> CreateAsync(ModuleInstallationRequest request, CancellationToken cancellationToken)
        {
            calls.Add("backup");
            if (fail) throw new ModuleDatabaseBackupException("backup failed");
            return Task.FromResult(new VerifiedModuleDatabaseBackup("backup.dump", "backup.sha256", "hash", DateTime.UtcNow));
        }
    }

    private sealed class FakePackageStore(List<string> calls) : IModuleInstallationPackageStore
    {
        public Task<StoredModulePackage> StoreAsync(ModuleInstallationPackage package, string storageRoot, ModuleTrustKeyRing trustKeyRing, CancellationToken cancellationToken)
        {
            calls.Add("store:" + package.ModuleId);
            return Task.FromResult(new StoredModulePackage(package.ModuleId, package.Version, package.ModuleId + ".zip", "hash", false));
        }
    }

    private sealed class FakeMigrations(List<string> calls) : IModuleInstallationMigrations
    {
        public Task<IReadOnlyList<ModuleMigrationExecutionResult>> ApplyAsync(ModuleInstallationRequest request, IReadOnlyList<StoredModulePackage> packages, CancellationToken cancellationToken)
        {
            calls.Add("migrate");
            return Task.FromResult<IReadOnlyList<ModuleMigrationExecutionResult>>([]);
        }
    }

    private sealed class FailingPackageStore(List<string> calls) : IModuleInstallationPackageStore
    {
        public Task<StoredModulePackage> StoreAsync(ModuleInstallationPackage package, string storageRoot, ModuleTrustKeyRing trustKeyRing, CancellationToken cancellationToken)
        {
            calls.Add("store:" + package.ModuleId);
            throw new ModulePackageStoreException("storage_failed", "storage failed");
        }
    }
}
