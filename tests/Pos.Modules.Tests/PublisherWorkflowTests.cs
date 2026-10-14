using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pos.Modules.Packaging;
using Pos.Modules.Publisher;

namespace Pos.Modules.Tests;

public sealed class PublisherWorkflowTests
{
    [Fact]
    public void Publish_SignsValidatedUnsignedPackageWithPemAndExportsMatchingPublicKey()
    {
        using var rsa = RSA.Create(3072);
        var root = Directory.CreateTempSubdirectory("pos-module-pem-publisher-").FullName;
        try
        {
            var privateKeyPath = Path.Combine(root, "private.pem");
            var unsignedPath = Path.Combine(root, "unsigned.zip");
            var signedPath = Path.Combine(root, "signed.zip");
            var publicKeyPath = Path.Combine(root, "public.pem");
            File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
            File.WriteAllBytes(unsignedPath, ModulePackageBuilder.BuildUnsigned(
                ManifestJson,
                new Dictionary<string, byte[]> { ["module.txt"] = "contenido validado"u8.ToArray() },
                "pos-publisher-vps-test"));

            PublisherRunner.Run(PublisherArguments.Parse([
                "sign-package", "--input", unsignedPath, "--output", signedPath,
                "--private-key-pem", privateKeyPath]));
            PublisherRunner.Run(PublisherArguments.Parse([
                "export-public-key", "--private-key-pem", privateKeyPath, "--output", publicKeyPath]));

            using var package = File.OpenRead(signedPath);
            var verified = ModulePackageVerifier.Verify(package, new Dictionary<string, string>
            {
                ["pos-publisher-vps-test"] = File.ReadAllText(publicKeyPath)
            });

            Assert.Equal("sample-module", verified.Module.Id);
            Assert.Equal("pos-publisher-vps-test", verified.SignerKeyId);
            Assert.Contains(verified.Files, file => file.Path == "module.txt");
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", File.ReadAllText(publicKeyPath));
            Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(publicKeyPath), StringComparison.Ordinal);

            using var wrongKey = RSA.Create(2048);
            package.Position = 0;
            Assert.Throws<ModulePackageVerificationException>(() =>
                ModulePackageVerifier.Verify(package, new Dictionary<string, string>
                {
                    ["pos-publisher-vps-test"] = wrongKey.ExportSubjectPublicKeyInfoPem()
                }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SignPackage_RejectsTamperedUnsignedPayloadAndAlreadySignedArchive()
    {
        using var rsa = RSA.Create(2048);
        var root = Directory.CreateTempSubdirectory("pos-module-pem-reject-").FullName;
        try
        {
            var privateKeyPath = Path.Combine(root, "private.pem");
            var unsignedPath = Path.Combine(root, "unsigned.zip");
            var tamperedPath = Path.Combine(root, "tampered.zip");
            var alreadySignedPath = Path.Combine(root, "already-signed.zip");
            var outputPath = Path.Combine(root, "output.zip");
            File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
            var unsignedBytes = ModulePackageBuilder.BuildUnsigned(
                ManifestJson,
                new Dictionary<string, byte[]> { ["module.txt"] = "original"u8.ToArray() },
                "pos-publisher-vps-test");
            File.WriteAllBytes(unsignedPath, unsignedBytes);
            File.WriteAllBytes(tamperedPath, ReplaceEntry(unsignedBytes, "module.txt", "alterado"u8.ToArray()));
            File.WriteAllBytes(alreadySignedPath, ModulePackageBuilder.Build(
                ManifestJson,
                new Dictionary<string, byte[]> { ["module.txt"] = "original"u8.ToArray() },
                "pos-publisher-vps-test", rsa));

            using (var unsignedPackage = File.OpenRead(unsignedPath))
            {
                Assert.Throws<ModulePackageVerificationException>(() => ModulePackageVerifier.Verify(
                    unsignedPackage,
                    new Dictionary<string, string> { ["pos-publisher-vps-test"] = rsa.ExportSubjectPublicKeyInfoPem() }));
            }

            var tampered = PublisherArguments.Parse([
                "sign-package", "--input", tamperedPath, "--output", outputPath,
                "--private-key-pem", privateKeyPath]);
            var alreadySigned = PublisherArguments.Parse([
                "sign-package", "--input", alreadySignedPath, "--output", outputPath,
                "--private-key-pem", privateKeyPath]);

            Assert.Throws<ModulePackageVerificationException>(() => PublisherRunner.Run(tampered));
            Assert.Throws<ModulePackageVerificationException>(() => PublisherRunner.Run(alreadySigned));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publish_ExportsPublicKeyAndCreatesVerifiablePackageFromExplicitFiles()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=POS publisher workflow test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var root = Directory.CreateTempSubdirectory("pos-module-publisher-").FullName;
        try
        {
            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            var manifestPath = Path.Combine(root, "module.manifest.json");
            var filesListPath = Path.Combine(root, "package-files.json");
            var packagePath = Path.Combine(root, "module.zip");
            var publicKeyPath = Path.Combine(root, "public.pem");
            File.WriteAllText(manifestPath, """
                {
                  "id": "sample-module",
                  "version": "1.0.0",
                  "hostApi": { "min": 1, "max": 1 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
                  "dependencies": [],
                  "permissions": ["Sample.View"],
                  "entitlement": null,
                  "migrations": []
                }
                """);
            File.WriteAllText(Path.Combine(stage, "module.txt"), "contenido de muestra");
            File.WriteAllText(filesListPath, "{\"files\":[\"module.txt\"]}");

            PublisherRunner.Run(new PublisherOptions(
                PublisherCommand.Build, manifestPath, stage, filesListPath, packagePath,
                "pos-publisher-e2e", "unused-in-memory-certificate", StoreLocation.CurrentUser), certificate);
            PublisherRunner.Run(new PublisherOptions(
                PublisherCommand.ExportPublicKey, null, null, null, publicKeyPath,
                null, "unused-in-memory-certificate", StoreLocation.CurrentUser), certificate);

            using var package = File.OpenRead(packagePath);
            var verified = ModulePackageVerifier.Verify(package, new Dictionary<string, string>
            {
                ["pos-publisher-e2e"] = File.ReadAllText(publicKeyPath)
            });

            Assert.Equal("sample-module", verified.Module.Id);
            Assert.Equal(2, verified.Files.Count);
            Assert.Contains(verified.Files, file => file.Path == "module.txt");
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", File.ReadAllText(publicKeyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string ManifestJson = """
        {
          "id": "sample-module",
          "version": "1.0.0",
          "hostApi": { "min": 1, "max": 1 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
          "dependencies": [],
          "permissions": ["Sample.View"],
          "entitlement": null,
          "migrations": []
        }
        """;

    private static byte[] ReplaceEntry(byte[] packageBytes, string path, byte[] replacement)
    {
        using var package = new MemoryStream();
        package.Write(packageBytes);
        package.Position = 0;
        using (var archive = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            archive.GetEntry(path)!.Delete();
            using var output = archive.CreateEntry(path).Open();
            output.Write(replacement);
        }
        return package.ToArray();
    }
}
