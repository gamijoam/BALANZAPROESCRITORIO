using Pos.Modules.Abstractions;

namespace Pos.Modules.Tests;

public sealed class ModuleManifestTests
{
    [Fact]
    public void ParseAndValidate_ExposesVersionedDesktopEntryPoint()
    {
        const string json = """
            {
              "id": "warehouses", "version": "2.0.0",
              "hostApi": { "min": 1, "max": 1 },
              "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
              "desktopApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
              "desktopEntryAssembly": "Pos.Modules.Warehouses.Desktop.dll",
              "desktopEntryType": "Pos.Modules.Warehouses.Desktop.WarehousesDesktopModule",
              "entryAssembly": "Pos.Modules.Warehouses.dll",
              "entryType": "Pos.Modules.Warehouses.WarehouseModule",
              "dependencies": [], "permissions": ["Warehouse.View"],
              "entitlement": null, "migrations": ["warehouses:0001"]
            }
            """;

        var manifest = ModuleManifestReader.ParseAndValidate(json);
        var manifestType = typeof(ModuleManifest);
        var desktopAssembly = manifestType.GetProperty("DesktopEntryAssembly");
        var desktopType = manifestType.GetProperty("DesktopEntryType");
        var desktopApi = manifestType.GetProperty("DesktopApi");

        Assert.NotNull(desktopAssembly);
        Assert.NotNull(desktopType);
        Assert.NotNull(desktopApi);
        Assert.Equal("Pos.Modules.Warehouses.Desktop.dll", desktopAssembly!.GetValue(manifest));
        Assert.Equal("Pos.Modules.Warehouses.Desktop.WarehousesDesktopModule", desktopType!.GetValue(manifest));
        Assert.Equal(new Version(1, 0, 0), desktopApi!.GetValue(manifest)!.GetType().GetProperty("Min")!.GetValue(desktopApi.GetValue(manifest)));
    }

    [Theory]
    [InlineData("desktopEntryAssembly", "Pos.Modules.Warehouses.Desktop.dll", null)]
    [InlineData("desktopEntryType", null, "Pos.Modules.Warehouses.Desktop.Module")]
    [InlineData("desktopApi", "Pos.Modules.Warehouses.Desktop.dll", "Pos.Modules.Warehouses.Desktop.Module")]
    public void ParseAndValidate_RejectsIncompleteDesktopModuleDeclaration(string field, string? assembly, string? type)
    {
        var desktopFields = field switch
        {
            "desktopEntryAssembly" => $"\"desktopEntryAssembly\":\"{assembly}\"",
            "desktopEntryType" => $"\"desktopEntryType\":\"{type}\"",
            _ => $"\"desktopEntryAssembly\":\"{assembly}\",\"desktopEntryType\":\"{type}\""
        };
        var json = $$"""
            { "id":"warehouses", "version":"1.0.0", "hostApi":{"min":1,"max":1},
              "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
              {{desktopFields}},
              "dependencies":[], "permissions":[], "entitlement":null, "migrations":[] }
            """;

        Assert.Throws<ModuleManifestException>(() => ModuleManifestReader.ParseAndValidate(json));
    }

    [Fact]
    public void ParseAndValidate_RequiresClientApiCompatibilityRange()
    {
        const string json = """
            {
              "id": "warehouses",
              "version": "1.2.0",
              "hostApi": { "min": 1, "max": 2 },
              "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
              "dependencies": [],
              "permissions": ["Warehouse.View"],
              "entitlement": null,
              "migrations": []
            }
            """;

        var manifest = ModuleManifestReader.ParseAndValidate(json);

        Assert.Equal(new Version(1, 0, 0), manifest.ClientApi!.Min);
        Assert.Equal(new Version(2, 0, 0), manifest.ClientApi.MaxExclusive);
    }

    [Fact]
    public void ParseAndValidate_RejectsMissingOrInvertedClientApiRange()
    {
        const string missing = """
            { "id":"warehouses", "version":"1.0.0", "hostApi":{"min":1,"max":1},
              "dependencies":[], "permissions":[], "entitlement":null, "migrations":[] }
            """;
        const string inverted = """
            { "id":"warehouses", "version":"1.0.0", "hostApi":{"min":1,"max":1},
              "clientApi":{"min":"2.0.0","maxExclusive":"2.0.0"},
              "dependencies":[], "permissions":[], "entitlement":null, "migrations":[] }
            """;

        Assert.Throws<ModuleManifestException>(() => ModuleManifestReader.ParseAndValidate(missing));
        Assert.Throws<ModuleManifestException>(() => ModuleManifestReader.ParseAndValidate(inverted));
    }

    [Fact]
    public void ParseAndValidate_RequiresSafeEntryAssemblyAndTypeAsAPair()
    {
        const string valid = """
            { "id":"warehouses", "version":"1.0.0", "hostApi":{"min":1,"max":1},
              "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
              "entryAssembly":"Pos.Modules.Warehouses.dll", "entryType":"Pos.Modules.Warehouses.WarehousesModule",
              "dependencies":[], "permissions":[], "entitlement":null, "migrations":[] }
            """;
        const string unsafePath = """
            { "id":"warehouses", "version":"1.0.0", "hostApi":{"min":1,"max":1},
              "clientApi":{"min":"1.0.0","maxExclusive":"2.0.0"},
              "entryAssembly":"..\\evil.dll", "entryType":"Pos.Modules.Warehouses.WarehousesModule",
              "dependencies":[], "permissions":[], "entitlement":null, "migrations":[] }
            """;

        Assert.Equal("Pos.Modules.Warehouses.dll", ModuleManifestReader.ParseAndValidate(valid).EntryAssembly);
        Assert.Throws<ModuleManifestException>(() => ModuleManifestReader.ParseAndValidate(unsafePath));
    }

    [Fact]
    public void TerminalCompatibility_RequiresEveryRegisteredTerminalToReportAnAcceptedVersion()
    {
        var policy = new ModuleTerminalCompatibilityPolicy();
        var range = new ModuleClientApiRange(new Version(1, 2, 0), new Version(2, 0, 0));

        var result = policy.Evaluate(range, [new Version(1, 2, 3), null]);

        Assert.False(result.IsCompatible);
        Assert.Equal(1, result.UnreportedTerminalCount);
        Assert.Empty(result.IncompatibleVersions);
    }

    [Fact]
    public void TerminalCompatibility_RejectsVersionsOutsideTheDeclaredClientRange()
    {
        var result = new ModuleTerminalCompatibilityPolicy().Evaluate(
            new ModuleClientApiRange(new Version(1, 2, 0), new Version(2, 0, 0)),
            [new Version(1, 1, 9), new Version(2, 0, 0)]);

        Assert.False(result.IsCompatible);
        Assert.Equal(2, result.RegisteredTerminalCount);
        Assert.Equal(new[] { "1.1.9", "2.0.0" }, result.IncompatibleVersions);
    }

    [Fact]
    public void ClientVersionParser_AllowsOldClientsToOmitVersionButRejectsMalformedValues()
    {
        Assert.True(ClientApiVersionParser.TryParseOptional(null, out var missing));
        Assert.Null(missing);
        Assert.True(ClientApiVersionParser.TryParseOptional("1.2.3", out var parsed));
        Assert.Equal(new Version(1, 2, 3), parsed);
        Assert.False(ClientApiVersionParser.TryParseOptional("1.2", out _));
    }

    [Fact]
    public void ParseAndValidate_AcceptsValidManifest()
    {
        const string json = """
            {
              "id": "warehouses",
              "version": "1.2.0",
              "hostApi": { "min": 1, "max": 2 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
              "dependencies": [
                { "id": "catalog", "minVersion": "1.0.0", "maxVersionExclusive": "2.0.0" }
              ],
              "permissions": ["Warehouse.View", "Warehouse.Transfer"],
              "entitlement": "warehouses.advanced",
              "migrations": ["warehouses:0001"]
            }
            """;

        var result = ModuleManifestReader.ParseAndValidate(json);

        Assert.Equal("warehouses", result.Id);
        Assert.Equal(new Version(1, 2, 0), result.Version);
        Assert.Single(result.Dependencies);
    }

    [Theory]
    [InlineData("not an id", "1.0.0")]
    [InlineData("warehouses", "1.0")]
    [InlineData("warehouses", "01.0.0")]
    [InlineData("warehouses", "1.0.0-beta")]
    public void ParseAndValidate_RejectsInvalidModuleIdOrVersion(string id, string version)
    {
        var json = $$"""
            {
              "id": "{{id}}",
              "version": "{{version}}",
              "hostApi": { "min": 1, "max": 1 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
              "dependencies": [],
              "permissions": ["Warehouse.View"],
              "entitlement": "warehouses.advanced",
              "migrations": []
            }
            """;

        Assert.Throws<ModuleManifestException>(() => ModuleManifestReader.ParseAndValidate(json));
    }

    [Fact]
    public void ParseAndValidate_RejectsRepeatedPermissions()
    {
        const string json = """
            {
              "id": "warehouses",
              "version": "1.0.0",
              "hostApi": { "min": 1, "max": 1 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
              "dependencies": [],
              "permissions": ["Warehouse.View", "Warehouse.View"],
              "entitlement": "warehouses.advanced",
              "migrations": []
            }
            """;

        Assert.Throws<ModuleManifestException>(() => ModuleManifestReader.ParseAndValidate(json));
    }
}

public sealed class ModuleDependencyResolverTests
{
    [Fact]
    public void Resolve_ReturnsDependenciesBeforeRequestedModule()
    {
        var catalog = Manifest("catalog", "1.2.0");
        var warehouse = Manifest("warehouses", "1.0.0",
            new ModuleDependency("catalog", new Version(1, 0, 0), new Version(2, 0, 0)));

        var resolved = ModuleDependencyResolver.Resolve([warehouse, catalog], ["warehouses"], hostApiVersion: 1);

        Assert.Equal(["catalog", "warehouses"], resolved.Select(module => module.Id));
    }

    [Fact]
    public void Resolve_RejectsMissingDependencyAndCycle()
    {
        var missing = Manifest("warehouses", "1.0.0",
            new ModuleDependency("catalog", new Version(1, 0, 0), new Version(2, 0, 0)));
        Assert.Throws<ModuleResolutionException>(() =>
            ModuleDependencyResolver.Resolve([missing], ["warehouses"], hostApiVersion: 1));

        var a = Manifest("a", "1.0.0", new ModuleDependency("b", new Version(1, 0, 0), new Version(2, 0, 0)));
        var b = Manifest("b", "1.0.0", new ModuleDependency("a", new Version(1, 0, 0), new Version(2, 0, 0)));
        Assert.Throws<ModuleResolutionException>(() =>
            ModuleDependencyResolver.Resolve([a, b], ["a"], hostApiVersion: 1));
    }

    [Fact]
    public void Resolve_RejectsDuplicateCatalogIdsAndDuplicateRequests()
    {
        var duplicate1 = Manifest("catalog", "1.0.0");
        var duplicate2 = Manifest("catalog", "1.1.0");
        Assert.Throws<ModuleResolutionException>(() =>
            ModuleDependencyResolver.Resolve([duplicate1, duplicate2], ["catalog"], hostApiVersion: 1));

        Assert.Throws<ModuleResolutionException>(() =>
            ModuleDependencyResolver.Resolve([duplicate1], ["catalog", "catalog"], hostApiVersion: 1));
    }

    [Fact]
    public void Resolve_RejectsIncompatibleDependencyVersionAndHostApi()
    {
        var catalog = Manifest("catalog", "2.0.0");
        var warehouse = Manifest("warehouses", "1.0.0",
            new ModuleDependency("catalog", new Version(1, 0, 0), new Version(2, 0, 0)));
        Assert.Throws<ModuleResolutionException>(() =>
            ModuleDependencyResolver.Resolve([warehouse, catalog], ["warehouses"], hostApiVersion: 1));

        var api2 = ManifestWithHost("api2", "1.0.0", 2, 3);
        Assert.Throws<ModuleResolutionException>(() =>
            ModuleDependencyResolver.Resolve([api2], ["api2"], hostApiVersion: 1));
    }

    private static ModuleManifest Manifest(string id, string version, params ModuleDependency[] dependencies) =>
        Manifest(id, version, dependencies, 1, 1);

    private static ModuleManifest Manifest(string id, string version, ModuleDependency[] dependencies, int hostMin, int hostMax) =>
        new(id, Version.Parse(version), new ModuleHostApiRange(hostMin, hostMax), new ModuleClientApiRange(new Version(1, 0, 0), new Version(2, 0, 0)), null, null, null, null, null, dependencies, [], null, []);

    private static ModuleManifest ManifestWithHost(string id, string version, int hostMin, int hostMax) =>
        Manifest(id, version, [], hostMin, hostMax);

    private static ModuleManifest Manifest(string id, string version, ModuleDependency dependency, int hostMin, int hostMax) =>
        Manifest(id, version, [dependency], hostMin, hostMax);
}
