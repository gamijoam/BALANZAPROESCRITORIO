using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Application.Licensing;
using Pos.Domain.Licensing;

namespace Pos.Integration.Tests;

public sealed class PosLicenseDocumentVerifierTests
{
    private static readonly Guid InstallationId = Guid.Parse("39c1657a-49b4-4c1a-9f56-7c3e0eb788d5");
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private const string KeyId = "license-2026";

    [Fact]
    public void Verify_AcceptsTrustedSignatureAndMatchingInstallation()
    {
        using var signer = RSA.Create(2048);
        var verifier = CreateVerifier(signer, KeyId);
        var document = CreateDocument(signer, KeyId, InstallationId);

        var license = verifier.Verify(document, InstallationId, NowUtc);

        Assert.Equal("LIC-2026-00145", license.LicenseId);
        Assert.Equal(3, license.MaxTerminals);
        Assert.Contains("warehouses", license.Entitlements);
    }

    [Fact]
    public void Verify_RejectsDifferentInstallation()
    {
        using var signer = RSA.Create(2048);
        var verifier = CreateVerifier(signer, KeyId);
        var document = CreateDocument(signer, KeyId, InstallationId);

        Assert.Throws<PosLicenseDocumentException>(() => verifier.Verify(document, Guid.NewGuid(), NowUtc));
    }

    [Fact]
    public void Verify_RejectsUnknownSigningKey()
    {
        using var signer = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var verifier = CreateVerifier(other, "other-key");
        var document = CreateDocument(signer, KeyId, InstallationId);

        Assert.Throws<PosLicenseDocumentException>(() => verifier.Verify(document, InstallationId, NowUtc));
    }

    [Fact]
    public void Verify_RejectsModifiedSignature()
    {
        using var signer = RSA.Create(2048);
        var verifier = CreateVerifier(signer, KeyId);
        var envelope = JsonNode.Parse(CreateDocument(signer, KeyId, InstallationId))!;
        var signature = Convert.FromBase64String(envelope["signature"]!.GetValue<string>());
        signature[0] ^= 0x01;
        envelope["signature"] = Convert.ToBase64String(signature);

        Assert.Throws<PosLicenseDocumentException>(() => verifier.Verify(
            JsonSerializer.SerializeToUtf8Bytes(envelope), InstallationId, NowUtc));
    }

    [Fact]
    public void Verify_RejectsInvalidTerminalLimit()
    {
        using var signer = RSA.Create(2048);
        var verifier = CreateVerifier(signer, KeyId);
        var document = CreateDocument(signer, KeyId, InstallationId, maxTerminals: 0);

        Assert.Throws<PosLicenseDocumentException>(() => verifier.Verify(document, InstallationId, NowUtc));
    }

    private static PosLicenseDocumentVerifier CreateVerifier(RSA key, string keyId) =>
        new(new Dictionary<string, string> { [keyId] = key.ExportSubjectPublicKeyInfoPem() });

    private static byte[] CreateDocument(RSA signer, string keyId, Guid installationId, int maxTerminals = 3)
    {
        var license = new PosLicense(
            1,
            "LIC-2026-00145",
            "J-12345678-9",
            "Comercio Ejemplo C.A.",
            installationId,
            "PRO_MULTICAJA",
            maxTerminals,
            ["pos.core", "warehouses", "advanced-reports"],
            NowUtc.AddDays(-1),
            NowUtc.AddDays(30));
        var payload = JsonSerializer.SerializeToUtf8Bytes(license, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var signedBytes = PosLicenseDocumentVerifier.BuildSignedBytes(keyId, payload);
        var signature = signer.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = new
        {
            schemaVersion = 1,
            keyId,
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature)
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }
}
