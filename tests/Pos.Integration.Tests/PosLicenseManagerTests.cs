using System.Security.Cryptography;
using System.Text.Json;
using Pos.Application.Licensing;
using Pos.Domain.Licensing;
using Pos.Infrastructure.Licensing;

namespace Pos.Integration.Tests;

public sealed class PosLicenseManagerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private const string KeyId = "license-2026";

    [Fact]
    public void GetStatus_CreatesStableInstallationIdAndReportsMissingLicense()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-manager-");
        try
        {
            using var signer = RSA.Create(2048);
            var keyRingPath = WriteKeyRing(directory.FullName, signer);
            var first = new PosLicenseManager(directory.FullName, keyRingPath).GetStatus(NowUtc);
            var second = new PosLicenseManager(directory.FullName, keyRingPath).GetStatus(NowUtc);

            Assert.Equal("Missing", first.State);
            Assert.NotEqual(Guid.Empty, first.InstallationId);
            Assert.Equal(first.InstallationId, second.InstallationId);
            Assert.Empty(new PosLicenseManager(directory.FullName, keyRingPath).GetGrantedEntitlements(NowUtc));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Install_PersistsVerifiedLicenseAndCanReloadIt()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-manager-");
        try
        {
            using var signer = RSA.Create(2048);
            var keyRingPath = WriteKeyRing(directory.FullName, signer);
            var manager = new PosLicenseManager(directory.FullName, keyRingPath);
            var installationId = manager.GetInstallationId();
            var document = PosLicenseDocumentVerifierTestsDocument(signer, installationId);

            var status = manager.Install(document, NowUtc);
            var reloaded = new PosLicenseManager(directory.FullName, keyRingPath).GetStatus(NowUtc);

            Assert.Equal("Active", status.State);
            Assert.Equal("LIC-2026-00145", status.LicenseId);
            Assert.Equal(status.InstallationId, reloaded.InstallationId);
            Assert.Equal(status.LicenseId, reloaded.LicenseId);
            Assert.Equal(["pos.core", "warehouses"], reloaded.Entitlements);
            Assert.Equal(new HashSet<string>(["pos.core", "warehouses"], StringComparer.Ordinal),
                new PosLicenseManager(directory.FullName, keyRingPath).GetGrantedEntitlements(NowUtc));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Install_RejectsLicenseSignedForAnotherInstallation()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-manager-");
        try
        {
            using var signer = RSA.Create(2048);
            var keyRingPath = WriteKeyRing(directory.FullName, signer);
            var manager = new PosLicenseManager(directory.FullName, keyRingPath);
            var document = PosLicenseDocumentVerifierTestsDocument(signer, Guid.NewGuid());

            Assert.Throws<PosLicenseDocumentException>(() => manager.Install(document, NowUtc));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "license.poslic")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GrantedEntitlements_RemainAvailableDuringGraceAndAreRemovedWhenLocked()
    {
        var directory = Directory.CreateTempSubdirectory("pos-license-manager-");
        try
        {
            using var signer = RSA.Create(2048);
            var keyRingPath = WriteKeyRing(directory.FullName, signer);
            var manager = new PosLicenseManager(directory.FullName, keyRingPath);
            var expiresAtUtc = NowUtc.AddDays(-1);
            var document = PosLicenseDocumentVerifierTestsDocument(signer, manager.GetInstallationId(), expiresAtUtc);

            var graceStatus = manager.Install(document, NowUtc);
            var lockedAtUtc = expiresAtUtc.AddDays(2);

            Assert.Equal("Grace", graceStatus.State);
            Assert.Contains("warehouses", manager.GetGrantedEntitlements(NowUtc));
            Assert.Equal("Locked", manager.GetStatus(lockedAtUtc).State);
            Assert.Empty(manager.GetGrantedEntitlements(lockedAtUtc));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string WriteKeyRing(string directory, RSA signer)
    {
        var path = Path.Combine(directory, "trusted-license-keys.json");
        var document = new
        {
            schemaVersion = 1,
            keys = new[] { new { keyId = KeyId, publicKeyPem = signer.ExportSubjectPublicKeyInfoPem() } }
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(document));
        return path;
    }

    private static byte[] PosLicenseDocumentVerifierTestsDocument(RSA signer, Guid installationId, DateTimeOffset? expiresAtUtc = null)
    {
        var expires = expiresAtUtc ?? NowUtc.AddDays(30);
        var issued = expiresAtUtc.HasValue ? expires.AddDays(-30) : NowUtc.AddDays(-1);
        var license = new PosLicense(1, "LIC-2026-00145", "J-12345678-9", "Comercio Ejemplo C.A.", installationId,
            "PRO_MULTICAJA", 3, ["pos.core", "warehouses"], issued, expires);
        var payload = JsonSerializer.SerializeToUtf8Bytes(license, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var signature = signer.SignData(PosLicenseDocumentVerifier.BuildSignedBytes(KeyId, payload),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            keyId = KeyId,
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature)
        });
    }
}
