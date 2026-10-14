using Pos.Modules.Publisher;
using System.Security.Cryptography.X509Certificates;

namespace Pos.Modules.Tests;

public sealed class ModulePublisherInputTests
{
    [Fact]
    public void Parse_BuildCommandRequiresExplicitPathsAndCertificateIdentity()
    {
        var options = PublisherArguments.Parse([
            "build",
            "--manifest", "module.manifest.json",
            "--stage", "stage",
            "--files", "package-files.json",
            "--output", "module.zip",
            "--key-id", "pos-publisher-2026",
            "--thumbprint", "A1 B2 C3",
            "--store", "CurrentUser"
        ]);

        Assert.Equal(PublisherCommand.Build, options.Command);
        Assert.Equal("pos-publisher-2026", options.KeyId);
        Assert.Equal("A1B2C3", options.CertificateThumbprint);
        Assert.Equal(StoreLocation.CurrentUser, options.StoreLocation);
    }

    [Fact]
    public void Parse_RejectsMissingDuplicateAndUnknownOptions()
    {
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse(["build", "--manifest", "module.json"]));
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse([
            "build", "--manifest", "one.json", "--manifest", "two.json"]));
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse(["export-public-key", "--thumbprint", "abcd", "--unknown", "x"]));
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse([
            "export-public-key", "--thumbprint", "abcd", "--output", "key.pem", "--store", "999"]));
    }

    [Fact]
    public void Parse_ExportPublicKeyNeedsOnlyCertificateAndDestination()
    {
        var options = PublisherArguments.Parse([
            "export-public-key", "--thumbprint", "A1B2C3", "--output", "trusted.pem"]);

        Assert.Equal(PublisherCommand.ExportPublicKey, options.Command);
        Assert.Equal(StoreLocation.CurrentUser, options.StoreLocation);
        Assert.Null(options.ManifestPath);
    }

    [Fact]
    public void Parse_BuildUnsignedAndSignPackageAcceptPemInputsWithoutWindowsCertificate()
    {
        var build = PublisherArguments.Parse([
            "build-unsigned", "--manifest", "module.json", "--stage", "stage",
            "--files", "files.json", "--output", "unsigned.zip", "--key-id", "pos-vps-publisher"]);
        var sign = PublisherArguments.Parse([
            "sign-package", "--input", "unsigned.zip", "--output", "signed.zip",
            "--private-key-pem", "private.pem"]);
        var export = PublisherArguments.Parse([
            "export-public-key", "--private-key-pem", "private.pem", "--output", "public.pem"]);

        Assert.Equal(PublisherCommand.BuildUnsigned, build.Command);
        Assert.Equal("pos-vps-publisher", build.KeyId);
        Assert.Equal(PublisherCommand.SignPackage, sign.Command);
        Assert.Equal("unsigned.zip", sign.InputPath);
        Assert.Equal("private.pem", sign.PrivateKeyPemPath);
        Assert.Equal(PublisherCommand.ExportPublicKey, export.Command);
        Assert.Equal("private.pem", export.PrivateKeyPemPath);
    }

    [Fact]
    public void Parse_RejectsPemCommandsWithMissingOrUnexpectedOptions()
    {
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse([
            "sign-package", "--input", "unsigned.zip", "--output", "signed.zip"]));
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse([
            "sign-package", "--input", "unsigned.zip", "--output", "signed.zip",
            "--private-key-pem", "private.pem", "--thumbprint", "abcd"]));
        Assert.Throws<PublisherUsageException>(() => PublisherArguments.Parse([
            "build-unsigned", "--manifest", "module.json", "--stage", "stage",
            "--files", "files.json", "--output", "unsigned.zip", "--key-id", "bad id"]));
    }

    [Fact]
    public void PackageFileList_RequiresUniqueSafeAllowlistedFilesAndBlocksSecretFileNames()
    {
        var files = PackageFileList.Parse("""
            { "files": ["Pos.Modules.Warehouses.dll", "Desktop/Module.xaml"] }
            """);
        Assert.Equal(["Pos.Modules.Warehouses.dll", "Desktop/Module.xaml"], files);

        Assert.Throws<PublisherUsageException>(() => PackageFileList.Parse("""
            { "files": ["module.dll", "MODULE.DLL"] }
            """));
        Assert.Throws<PublisherUsageException>(() => PackageFileList.Parse("""
            { "files": ["../secrets.json"] }
            """));
        Assert.Throws<PublisherUsageException>(() => PackageFileList.Parse("""
            { "files": ["publisher.pfx"] }
            """));
        Assert.Throws<PublisherUsageException>(() => PackageFileList.Parse("""
            { "files": [], "files": ["module.dll"] }
            """));
        Assert.Throws<PublisherUsageException>(() => PackageFileList.Parse("""
            { "files": ["appsettings.Production.json"] }
            """));
        Assert.Throws<PublisherUsageException>(() => PackageFileList.Parse("""
            { "files": [".env.production"] }
            """));
    }
}
