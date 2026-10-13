using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Pos.Infrastructure.Modules;
using Pos.Modules.Abstractions;
using Pos.Modules.Packaging;
using Pos.Modules.Example;
using Pos.Modules.Warehouses;

namespace Pos.Integration.Tests;

public sealed class ModuleRuntimeLoaderTests
{
    [Fact]
    public void Load_AcceptsSignedWarehouseModuleAndPreservesPerEndpointPermissions()
    {
        using var signer = RSA.Create(2048);
        var assemblyPath = typeof(WarehouseModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        var manifestNode = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "warehouses-module.manifest.json")))!.AsObject();
        // This test exercises the server loader in isolation; desktop loading has dedicated WPF host tests.
        manifestNode.Remove("desktopEntryAssembly");
        manifestNode.Remove("desktopEntryType");
        manifestNode.Remove("desktopApi");
        var manifest = manifestNode.ToJsonString();
        var migration = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "warehouses-0001.json"));
        var bytes = ModulePackageBuilder.Build(manifest,
            new Dictionary<string, byte[]> { [assemblyName] = File.ReadAllBytes(assemblyPath), ["migrations/warehouses-0001.json"] = migration },
            "warehouse-test-publisher", signer);
        using var package = new MemoryStream(bytes, writable: false);

        var loaded = ModuleRuntimeLoader.Load(package, TrustRing("warehouse-test-publisher", signer));

        Assert.Equal("warehouses", loaded.Instance.ModuleId);
        Assert.Equal(11, loaded.Endpoints.Count);
        Assert.Contains(loaded.Endpoints, endpoint => endpoint.RequiredPermission == "Warehouse.View");
        Assert.Contains(loaded.Endpoints, endpoint => endpoint.RequiredPermission == "Warehouse.Manage");
        Assert.Contains(loaded.Endpoints, endpoint => endpoint.RequiredPermission == "Warehouse.Transfer");
    }

    [Fact]
    public async Task Load_ActivatesTheExecutableExampleAndServesItsStatusEndpoint()
    {
        using var signer = RSA.Create(2048);
        var assemblyPath = typeof(ExampleStatusModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        var manifest = $$"""
            {"id":"example-status","version":"1.0.0","hostApi":{"min":1,"max":1},
             "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
             "entryAssembly":"{{assemblyName}}","entryType":"{{typeof(ExampleStatusModule).FullName}}",
             "dependencies":[],"permissions":[],"entitlement":null,"migrations":["example-status:0001"]}
            """;
        var bytes = ModulePackageBuilder.Build(manifest,
            new Dictionary<string, byte[]>
            {
                [assemblyName] = File.ReadAllBytes(assemblyPath),
                ["migrations/example-status-0001.json"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "example-status-0001.json"))
            }, "publisher", signer);
        using var package = new MemoryStream(bytes, writable: false);
        var loaded = ModuleRuntimeLoader.Load(package, TrustRing("publisher", signer));
        var endpoint = Assert.Single(loaded.Endpoints);
        Assert.Equal("GET", endpoint.Method);
        Assert.Equal("status", endpoint.Route);

        var context = new DefaultHttpContext();
        using var response = new MemoryStream();
        context.Response.Body = response;
        await endpoint.InvokeAsync(context);
        response.Position = 0;
        using var document = await JsonDocument.ParseAsync(response);
        Assert.Equal("example-status", document.RootElement.GetProperty("moduleId").GetString());
        Assert.Equal("1.0.0", document.RootElement.GetProperty("version").GetString());
        Assert.Equal("active", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Load_VerifiesOfficialSignatureBeforeCreatingTheModuleAndItsRoutes()
    {
        using var signer = RSA.Create(2048);
        using var package = CreatePackage(signer, typeof(TestRuntimeModule));

        var loaded = ModuleRuntimeLoader.Load(package, TrustRing("publisher", signer));

        Assert.Equal("runtime-fixture", loaded.Instance.ModuleId);
        var endpoint = Assert.Single(loaded.Endpoints);
        Assert.Equal("GET", endpoint.Method);
        Assert.Equal("status", endpoint.Route);
    }

    [Fact]
    public void Load_RejectsAnUnknownPublisherBeforeLoadingCode()
    {
        using var signer = RSA.Create(2048);
        using var other = RSA.Create(2048);
        using var package = CreatePackage(signer, typeof(TestRuntimeModule));

        Assert.Throws<ModulePackageVerificationException>(() => ModuleRuntimeLoader.Load(package, TrustRing("other", other)));
    }

    private static MemoryStream CreatePackage(RSA signer, Type moduleType)
    {
        var assemblyPath = typeof(TestRuntimeModule).Assembly.Location;
        var assemblyBytes = File.ReadAllBytes(assemblyPath);
        var assemblyName = Path.GetFileName(assemblyPath);
        var manifest = $$"""
            {"id":"runtime-fixture","version":"1.0.0","hostApi":{"min":1,"max":1},
             "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
             "entryAssembly":"{{assemblyName}}","entryType":"{{moduleType.FullName}}",
             "dependencies":[],"permissions":[],"entitlement":null,"migrations":[]}
            """;
        var bytes = ModulePackageBuilder.Build(manifest,
            new Dictionary<string, byte[]> { [assemblyName] = assemblyBytes }, "publisher", signer);
        return new MemoryStream(bytes, writable: false);
    }

    private static ModuleTrustKeyRing TrustRing(string keyId, RSA key) => ModuleTrustKeyRing.Parse($$"""
        {"schemaVersion":1,"keys":[{"keyId":"{{keyId}}","publicKeyPem":{{JsonSerializer.Serialize(key.ExportSubjectPublicKeyInfoPem())}},"status":"Active"}]}
        """);
}

public sealed class TestRuntimeModule : IPosServerModule
{
    public string ModuleId => "runtime-fixture";

    public IReadOnlyList<PosModuleEndpoint> GetEndpoints() =>
    [new("GET", "status", context => context.Response.WriteAsync("active"))];
}
