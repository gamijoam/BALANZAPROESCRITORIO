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
