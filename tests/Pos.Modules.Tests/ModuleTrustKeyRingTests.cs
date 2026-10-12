using System.Security.Cryptography;
using System.Text.Json;
using Pos.Modules.Packaging;

namespace Pos.Modules.Tests;

public sealed class ModuleTrustKeyRingTests
{
    [Fact]
    public void Parse_AcceptsActiveAndRetiringKeysButExcludesRevokedKeys()
    {
        using var active = RSA.Create(2048);
        using var retiring = RSA.Create(2048);
        using var revoked = RSA.Create(2048);

        var ring = ModuleTrustKeyRing.Parse(KeyRingJson(
            ("publisher-2026", active.ExportSubjectPublicKeyInfoPem(), "Active"),
            ("publisher-2025", retiring.ExportSubjectPublicKeyInfoPem(), "Retiring"),
            ("publisher-compromised", revoked.ExportSubjectPublicKeyInfoPem(), "Revoked")));

        Assert.Equal(2, ring.VerifierPublicKeys.Count);
        Assert.Contains("publisher-2026", ring.VerifierPublicKeys.Keys);
        Assert.Contains("publisher-2025", ring.VerifierPublicKeys.Keys);
        Assert.DoesNotContain("publisher-compromised", ring.VerifierPublicKeys.Keys);

        using var oldPackage = new MemoryStream(ModulePackageBuilder.Build(ManifestJson, new Dictionary<string, byte[]>(), "publisher-2025", retiring));
        Assert.Equal("example-module", ModulePackageVerifier.Verify(oldPackage, ring).Module.Id);

        using var revokedPackage = new MemoryStream(ModulePackageBuilder.Build(ManifestJson, new Dictionary<string, byte[]>(), "publisher-compromised", revoked));
        Assert.Throws<ModulePackageVerificationException>(() => ModulePackageVerifier.Verify(revokedPackage, ring));
    }

    [Fact]
    public void Parse_RejectsDuplicateIdsUnknownStatusAndPrivateKeys()
    {
        using var key = RSA.Create(2048);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        Assert.Throws<ModuleTrustConfigurationException>(() => ModuleTrustKeyRing.Parse(KeyRingJson(
            ("publisher", publicKey, "Active"), ("publisher", publicKey, "Retiring"))));
        Assert.Throws<ModuleTrustConfigurationException>(() => ModuleTrustKeyRing.Parse(KeyRingJson(
            ("publisher", publicKey, "Unknown"))));
        Assert.Throws<ModuleTrustConfigurationException>(() => ModuleTrustKeyRing.Parse(KeyRingJson(
            ("publisher", key.ExportPkcs8PrivateKeyPem(), "Active"))));
    }

    [Fact]
    public void Store_FailsClosedAfterInvalidReloadAndRecoversAfterValidReload()
    {
        using var firstKey = RSA.Create(2048);
        using var secondKey = RSA.Create(2048);
        var first = ModuleTrustKeyRing.Parse(KeyRingJson(("publisher-a", firstKey.ExportSubjectPublicKeyInfoPem(), "Active")));
        var store = new ModuleTrustKeyRingStore(first);

        Assert.Throws<ModuleTrustConfigurationException>(() => store.Reload("{broken"));
        Assert.Throws<ModuleTrustConfigurationException>(() => _ = store.Current);

        store.Reload(KeyRingJson(("publisher-b", secondKey.ExportSubjectPublicKeyInfoPem(), "Active")));
        Assert.Contains("publisher-b", store.Current.VerifierPublicKeys.Keys);
        Assert.DoesNotContain("publisher-a", store.Current.VerifierPublicKeys.Keys);
    }

    [Fact]
    public void LoadFromFile_ReadsExternalTrustConfiguration()
    {
        using var key = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"pos-module-trust-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, KeyRingJson(("publisher", key.ExportSubjectPublicKeyInfoPem(), "Active")));
            var loaded = ModuleTrustKeyRing.LoadFromFile(path);
            Assert.Contains("publisher", loaded.VerifierPublicKeys.Keys);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string KeyRingJson(params (string Id, string Pem, string Status)[] keys) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            keys = keys.Select(key => new { keyId = key.Id, publicKeyPem = key.Pem, status = key.Status }).ToArray()
        });

    private const string ManifestJson = """
        {
          "id": "example-module",
          "version": "1.0.0",
          "hostApi": { "min": 1, "max": 1 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
          "dependencies": [],
          "permissions": ["Example.View"],
          "entitlement": null,
          "migrations": []
        }
        """;
}
