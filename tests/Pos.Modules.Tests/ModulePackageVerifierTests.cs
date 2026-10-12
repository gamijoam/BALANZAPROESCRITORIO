using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Pos.Modules.Packaging;

namespace Pos.Modules.Tests;

public sealed class ModulePackageVerifierTests
{
    [Fact]
    public void Build_CreatesVerifiedPackageWithStableInventory()
    {
        using var signingKey = RSA.Create(2048);
        var publicKeys = TrustedKey(signingKey);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["z-last.txt"] = Encoding.UTF8.GetBytes("last"),
            ["a-first.txt"] = Encoding.UTF8.GetBytes("first")
        };

        var first = ModulePackageBuilder.Build(ManifestJson, files, "pos-test-key", signingKey);
        var second = ModulePackageBuilder.Build(ManifestJson, files.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value), "pos-test-key", signingKey);

        using var stream = new MemoryStream(first);
        var verified = ModulePackageVerifier.Verify(stream, publicKeys);
        Assert.Equal("example-module", verified.Module.Id);
        Assert.Equal(["a-first.txt", "module.manifest.json", "z-last.txt"], verified.Files.Select(file => file.Path));
        Assert.Equal(ReadEntry(first, "package.index.json"), ReadEntry(second, "package.index.json"));
    }

    [Fact]
    public void Build_RejectsReservedOrUnsafeInputPaths()
    {
        using var signingKey = RSA.Create(2048);
        Assert.Throws<ModulePackageBuildException>(() => ModulePackageBuilder.Build(
            ManifestJson,
            new Dictionary<string, byte[]> { ["module.manifest.json"] = Encoding.UTF8.GetBytes("forged") },
            "pos-test-key",
            signingKey));

        Assert.Throws<ModulePackageBuildException>(() => ModulePackageBuilder.Build(
            ManifestJson,
            new Dictionary<string, byte[]> { ["../outside.txt"] = Encoding.UTF8.GetBytes("outside") },
            "pos-test-key",
            signingKey));

        using var publicOnlyKey = RSA.Create();
        publicOnlyKey.ImportFromPem(signingKey.ExportSubjectPublicKeyInfoPem());
        Assert.Throws<ModulePackageBuildException>(() => ModulePackageBuilder.Build(
            ManifestJson,
            new Dictionary<string, byte[]>(),
            "pos-test-key",
            publicOnlyKey));
    }

    [Fact]
    public void Verify_AcceptsTrustedSignedPackageAndReturnsManifest()
    {
        using var signingKey = RSA.Create(2048);
        using var package = CreatePackage(signingKey, [("module.manifest.json", ManifestJson), ("module.bin", "not executed")]);

        var result = ModulePackageVerifier.Verify(package, TrustedKey(signingKey));

        Assert.Equal("example-module", result.Module.Id);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal("pos-test-key", result.SignerKeyId);
    }

    [Fact]
    public void Verify_RejectsPackageWhenDeclaredDesktopEntryIsNotSignedIntoInventory()
    {
        using var signingKey = RSA.Create(2048);
        const string manifest = """
            { "id":"example-module", "version":"1.0.0", "hostApi":{"min":1,"max":1},
              "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
              "desktopEntryAssembly":"Example.Desktop.dll", "desktopEntryType":"Example.Desktop.Module",
              "desktopApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
              "dependencies":[], "permissions":[], "entitlement":null, "migrations":[] }
            """;
        using var package = new MemoryStream(ModulePackageBuilder.Build(manifest,
            new Dictionary<string, byte[]> { ["other.dll"] = [1, 2, 3] }, "pos-test-key", signingKey));

        var error = Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(package, TrustedKey(signingKey)));

        Assert.Contains("ensamblado de escritorio", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsUntrustedSignerAndModifiedIndex()
    {
        using var signingKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var package = CreatePackage(signingKey, [("module.manifest.json", ManifestJson)]);

        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(package, TrustedKey(otherKey)));

        package.Position = 0;
        var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ManifestJson))).ToLowerInvariant();
        var modifiedIndex = $$"""{"formatVersion":1,"signerKeyId":"pos-test-key","files":[{"path":"module.manifest.json","sha256":"{{manifestHash}}"}]} """;
        ReplaceEntry(package, "package.index.json", Encoding.UTF8.GetBytes(modifiedIndex));
        package.Position = 0;
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(package, TrustedKey(signingKey)));
    }

    [Fact]
    public void Verify_RejectsHashMismatchUndeclaredFilesAndUnsafePaths()
    {
        using var signingKey = RSA.Create(2048);

        using var badHash = CreatePackage(signingKey, [("module.manifest.json", ManifestJson)], hashOverride: "00");
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(badHash, TrustedKey(signingKey)));

        using var extra = CreatePackage(signingKey,
            [("module.manifest.json", ManifestJson), ("unexpected.txt", "extra")], omitLastFromIndex: true);
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(extra, TrustedKey(signingKey)));

        using var traversal = CreatePackage(signingKey,
            [("module.manifest.json", ManifestJson), ("../outside.txt", "outside")]);
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(traversal, TrustedKey(signingKey)));

        using var duplicate = CreatePackage(signingKey,
            [("module.manifest.json", ManifestJson), ("module.manifest.json", ManifestJson)]);
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(duplicate, TrustedKey(signingKey)));

        using var deviceName = CreatePackage(signingKey,
            [("module.manifest.json", ManifestJson), ("CON.txt", "device name")]);
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(deviceName, TrustedKey(signingKey)));
    }

    [Fact]
    public void Verify_RejectsUnsupportedFormatAndSpecialZipEntries()
    {
        using var signingKey = RSA.Create(2048);
        using var unsupported = CreatePackage(signingKey, [("module.manifest.json", ManifestJson)], formatVersion: 2);
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(unsupported, TrustedKey(signingKey)));

        using var symbolicLink = CreatePackage(signingKey, [("module.manifest.json", ManifestJson)], symbolicLinkPath: "module.manifest.json");
        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(symbolicLink, TrustedKey(signingKey)));
    }

    [Fact]
    public void Verify_RejectsPrivateKeyMaterialInVerifierConfiguration()
    {
        using var signingKey = RSA.Create(2048);
        using var package = CreatePackage(signingKey, [("module.manifest.json", ManifestJson)]);
        var invalidTrust = new Dictionary<string, string>
        {
            ["pos-test-key"] = signingKey.ExportPkcs8PrivateKeyPem()
        };

        Assert.Throws<ModulePackageVerificationException>(() => ModulePackageVerifier.Verify(package, invalidTrust));
    }

    [Fact]
    public void Verify_RejectsHighlyCompressedPayload()
    {
        using var signingKey = RSA.Create(2048);
        var compressedPayload = new string('x', 4 * 1024 * 1024);
        using var package = CreatePackage(signingKey,
            [("module.manifest.json", ManifestJson), ("payload.txt", compressedPayload)]);

        Assert.Throws<ModulePackageVerificationException>(() =>
            ModulePackageVerifier.Verify(package, TrustedKey(signingKey)));
    }

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

    private static Dictionary<string, string> TrustedKey(RSA key) =>
        new(StringComparer.Ordinal) { ["pos-test-key"] = key.ExportSubjectPublicKeyInfoPem() };

    private static MemoryStream CreatePackage(
        RSA signer,
        (string Path, string Contents)[] files,
        string? hashOverride = null,
        bool omitLastFromIndex = false,
        int formatVersion = 1,
        string? symbolicLinkPath = null)
    {
        var listedFiles = files.AsEnumerable();
        if (omitLastFromIndex)
            listedFiles = listedFiles.Take(files.Length - 1);

        var inventory = listedFiles.Select((file, index) => new
        {
            path = file.Path,
            sha256 = index == 0 && hashOverride is not null
                ? hashOverride
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Contents))).ToLowerInvariant()
        }).ToArray();
        var indexBytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        {
            formatVersion,
            signerKeyId = "pos-test-key",
            files = inventory
        }));
        var signature = signer.SignData(indexBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "package.index.json", indexBytes);
            WriteEntry(archive, "package.signature", Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                if (file.Path == symbolicLinkPath)
                    entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var output = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(file.Contents);
                output.Write(bytes);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] contents)
    {
        using var output = archive.CreateEntry(path).Open();
        output.Write(contents);
    }

    private static void ReplaceEntry(MemoryStream package, string path, byte[] contents)
    {
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            archive.GetEntry(path)?.Delete();
            WriteEntry(archive, path, contents);
        }
    }

    private static byte[] ReadEntry(byte[] package, string path)
    {
        using var stream = new MemoryStream(package);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var input = archive.GetEntry(path)!.Open();
        using var contents = new MemoryStream();
        input.CopyTo(contents);
        return contents.ToArray();
    }
}
