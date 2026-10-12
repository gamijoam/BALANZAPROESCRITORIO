using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pos.Modules.Packaging;
using Pos.Modules.Publisher;

namespace Pos.Modules.Tests;

public sealed class PublisherWorkflowTests
{
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
}
