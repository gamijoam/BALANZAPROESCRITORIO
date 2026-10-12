using System.Security.Cryptography;
using System.Text.Json;
using Pos.Modules.Packaging;

namespace Pos.Modules.Tests;

public sealed class ModulePackageStoreTests
{
    [Fact]
    public async Task Store_keeps_verified_package_in_versioned_folder_and_is_idempotent()
    {
        using var signingKey = RSA.Create(2048);
        var packageBytes = BuildPackage(signingKey, "sample", "1.2.3");
        var root = Directory.CreateTempSubdirectory("pos-modules-store-").FullName;
        try
        {
            var first = await ModulePackageStore.StoreAsync(new MemoryStream(packageBytes), root, TrustRing(signingKey));
            var second = await ModulePackageStore.StoreAsync(new MemoryStream(packageBytes), root, TrustRing(signingKey));

            Assert.Equal(Path.Combine(root, "sample", "1.2.3", "package.zip"), first.PackagePath);
            Assert.False(first.AlreadyStored);
            Assert.True(second.AlreadyStored);
            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(first.PackagePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Store_rejects_invalid_signature_without_leaving_install_artifacts()
    {
        using var signingKey = RSA.Create(2048);
        using var untrustedKey = RSA.Create(2048);
        var packageBytes = BuildPackage(signingKey, "sample", "1.0.0");
        var root = Directory.CreateTempSubdirectory("pos-modules-store-").FullName;
        try
        {
            await Assert.ThrowsAsync<ModulePackageVerificationException>(() =>
                ModulePackageStore.StoreAsync(new MemoryStream(packageBytes), root, TrustRing(untrustedKey)));

            Assert.Empty(Directory.EnumerateDirectories(root));
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Store_rejects_different_package_for_an_already_stored_version()
    {
        using var signingKey = RSA.Create(2048);
        var firstPackage = BuildPackage(signingKey, "sample", "1.0.0", "first");
        var conflictingPackage = BuildPackage(signingKey, "sample", "1.0.0", "second");
        var root = Directory.CreateTempSubdirectory("pos-modules-store-").FullName;
        try
        {
            await ModulePackageStore.StoreAsync(new MemoryStream(firstPackage), root, TrustRing(signingKey));

            var exception = await Assert.ThrowsAsync<ModulePackageStoreException>(() =>
                ModulePackageStore.StoreAsync(new MemoryStream(conflictingPackage), root, TrustRing(signingKey)));

            Assert.Equal("module_version_conflict", exception.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Concurrent_store_of_same_package_creates_one_versioned_artifact()
    {
        using var signingKey = RSA.Create(2048);
        var packageBytes = BuildPackage(signingKey, "sample", "1.0.0");
        var trustRing = TrustRing(signingKey);
        var root = Directory.CreateTempSubdirectory("pos-modules-store-").FullName;
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                ModulePackageStore.StoreAsync(new MemoryStream(packageBytes), root, trustRing)));

            Assert.Single(results, result => !result.AlreadyStored);
            Assert.Equal(3, results.Count(result => result.AlreadyStored));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "sample", "1.0.0")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] BuildPackage(RSA key, string id, string version, string fileContents = "module payload")
    {
        var manifest = $$"""
            {"id":"{{id}}","version":"{{version}}","hostApi":{"min":1,"max":1}, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },"dependencies":[],"permissions":["Sample.View"],"entitlement":null,"migrations":[]}
            """;
        return ModulePackageBuilder.Build(manifest, new Dictionary<string, byte[]> { ["payload/data.txt"] = System.Text.Encoding.UTF8.GetBytes(fileContents) }, "publisher", key);
    }

    private static ModuleTrustKeyRing TrustRing(RSA key) => ModuleTrustKeyRing.Parse($$"""
        {"schemaVersion":1,"keys":[{"keyId":"publisher","publicKeyPem":{{JsonSerializer.Serialize(key.ExportSubjectPublicKeyInfoPem())}},"status":"Active"}]}
        """);
}
