using System.Security.Cryptography;
using Pos.Desktop;
using Pos.Desktop.Presentation;
using Pos.Modules.Packaging;
using Pos.Modules.Warehouses.Desktop;

namespace Pos.Application.Tests;

public sealed class PosDesktopModuleLoaderTests
{
    [Fact]
    public void Load_VerifiesPackageAndLoadsMenuAndInventoryExtensionFromModuleAssembly()
    {
        using var signer = RSA.Create(2048);
        var descriptor = Descriptor(signer, ["Warehouse.View"]);
        using var api = new PosApiClient(new Uri("http://127.0.0.1:5155"));
        var loaded = PosDesktopModuleLoader.Load(CreatePackage(signer), descriptor, api, Guid.NewGuid(),
            (_, _) => Task.FromResult<IReadOnlyList<ProductInfo>>([]));
        try
        {
            Assert.Equal("warehouses", loaded.ModuleId);
            Assert.Equal("Warehouse.View", Assert.Single(loaded.Registry.Menus).RequiredPermission);
            Assert.Equal("Warehouse.View", Assert.Single(loaded.Registry.InventoryExtensions).RequiredPermission);
        }
        finally { loaded.LoadContext.Unload(); }
    }

    [Fact]
    public void Load_RejectsPackageSignedByAnUntrustedKey()
    {
        using var signer = RSA.Create(2048);
        using var otherSigner = RSA.Create(2048);
        var descriptor = Descriptor(otherSigner, ["Warehouse.View"]);
        using var api = new PosApiClient(new Uri("http://127.0.0.1:5155"));

        Assert.Throws<ModulePackageVerificationException>(() => PosDesktopModuleLoader.Load(CreatePackage(signer),
            descriptor, api, Guid.NewGuid(), (_, _) => Task.FromResult<IReadOnlyList<ProductInfo>>([])));
    }

    [Fact]
    public void Load_RejectsModuleRequiringNewerDesktopApi()
    {
        using var signer = RSA.Create(2048);
        var descriptor = Descriptor(signer, ["Warehouse.View"]);
        using var api = new PosApiClient(new Uri("http://127.0.0.1:5155"));
        var package = CreatePackage(signer, "2.0.0", "3.0.0");

        var error = Assert.Throws<InvalidDataException>(() => PosDesktopModuleLoader.Load(package,
            descriptor, api, Guid.NewGuid(), (_, _) => Task.FromResult<IReadOnlyList<ProductInfo>>([])));

        Assert.Contains("Desktop API", error.Message, StringComparison.Ordinal);
    }

    private static DesktopModuleClientInfo Descriptor(RSA signer, IReadOnlyList<string> permissions)
        => new("warehouses", "1.2.0", "test-publisher", signer.ExportSubjectPublicKeyInfoPem(), permissions);

    private static byte[] CreatePackage(RSA signer, string minApi = "1.0.0", string maxApi = "2.0.0")
    {
        var assemblyName = "Pos.Modules.Warehouses.Desktop.dll";
        var assemblyPath = typeof(WarehousesDesktopModule).Assembly.Location;
        var manifest = $$"""
            {"id":"warehouses","version":"1.2.0","hostApi":{"min":1,"max":1},
             "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
             "desktopEntryAssembly":"{{assemblyName}}",
             "desktopEntryType":"Pos.Modules.Warehouses.Desktop.WarehousesDesktopModule",
             "desktopApi":{"min":"{{minApi}}","maxExclusive":"{{maxApi}}"},
             "dependencies":[],"permissions":["Warehouse.View","Warehouse.Manage","Warehouse.Transfer"],
             "entitlement":null,"migrations":[]}
            """;
        return ModulePackageBuilder.Build(manifest,
            new Dictionary<string, byte[]> { [assemblyName] = File.ReadAllBytes(assemblyPath) },
            "test-publisher", signer);
    }
}
