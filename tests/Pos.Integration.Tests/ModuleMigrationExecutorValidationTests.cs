using System.Security.Cryptography;
using System.Text.Json;
using Pos.Infrastructure.Modules;
using Pos.Modules.Packaging;

namespace Pos.Integration.Tests;

public sealed class ModuleMigrationExecutorValidationTests
{
    [Fact]
    public async Task Rejects_untrusted_package_before_opening_database()
    {
        using var signer = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        var packagePath = WritePackage(signer, "sample:0001", """
            {"formatVersion":1,"operations":[{"type":"createTable","table":"records","columns":[{"name":"id","dataType":"uuid","nullable":false,"primaryKey":true}]}]}
            """);
        try
        {
            await Assert.ThrowsAsync<ModulePackageVerificationException>(() => new ModuleMigrationExecutor().ApplyAsync(
                "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none",
                [packagePath], "sample", 1, TrustRing(otherKey), new HashSet<string>(StringComparer.Ordinal)));
        }
        finally { File.Delete(packagePath); }
    }

    [Fact]
    public async Task Rejects_arbitrary_sql_migration_before_opening_database()
    {
        using var signer = RSA.Create(2048);
        var packagePath = WritePackage(signer, "sample:0001", """
            {"formatVersion":1,"operations":[{"type":"executeSql","sql":"DROP TABLE public.products"}]}
            """);
        try
        {
            var exception = await Assert.ThrowsAsync<ModuleMigrationExecutionException>(() => new ModuleMigrationExecutor().ApplyAsync(
                "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none",
                [packagePath], "sample", 1, TrustRing(signer), new HashSet<string>(StringComparer.Ordinal)));

            Assert.Contains("no es válida", exception.Message);
        }
        finally { File.Delete(packagePath); }
    }

    private static string WritePackage(RSA signer, string migrationId, string migrationJson)
    {
        var manifest = $$"""
            {"id":"sample","version":"1.0.0","hostApi":{"min":1,"max":1},"clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},"dependencies":[],"permissions":["Sample.View"],"entitlement":null,"migrations":["{{migrationId}}"]}
            """;
        var bytes = ModulePackageBuilder.Build(manifest,
            new Dictionary<string, byte[]> { ["migrations/sample-0001.json"] = System.Text.Encoding.UTF8.GetBytes(migrationJson) }, "publisher", signer);
        var path = Path.Combine(Path.GetTempPath(), $"pos-module-migration-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static ModuleTrustKeyRing TrustRing(RSA key) => ModuleTrustKeyRing.Parse($$"""
        {"schemaVersion":1,"keys":[{"keyId":"publisher","publicKeyPem":{{JsonSerializer.Serialize(key.ExportSubjectPublicKeyInfoPem())}},"status":"Active"}]}
        """);
}
