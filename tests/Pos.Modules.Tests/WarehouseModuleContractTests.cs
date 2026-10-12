using Pos.Modules.Abstractions;
using Pos.Modules.Warehouses;

namespace Pos.Modules.Tests;

public sealed class WarehouseModuleContractTests
{
    [Fact]
    public void Manifest_permissions_cover_every_endpoint_permission()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "warehouses", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(path));
        var endpointPermissions = new WarehouseModule().GetEndpoints()
            .Select(endpoint => endpoint.RequiredPermission)
            .Where(permission => permission is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("warehouses", manifest.Id);
        Assert.Equal(new Version(1, 2, 0), manifest.Version);
        Assert.Null(manifest.Entitlement);
        Assert.Empty(manifest.Dependencies);
        Assert.All(endpointPermissions, permission => Assert.Contains(permission, manifest.Permissions));
    }

    [Fact]
    public void Migration_is_additive_and_creates_isolated_inventory_and_transfer_tables()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "warehouses", "warehouses-0001.json");
        var definition = ModuleMigrationReader.Parse(File.ReadAllText(path));

        var tables = definition.Operations.OfType<CreateModuleTable>().Select(table => table.Table).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("warehouses", tables);
        Assert.Contains("warehouse_stock", tables);
        Assert.Contains("warehouse_transfers", tables);
        Assert.Contains("warehouse_transfer_lines", tables);
        Assert.Contains("terminal_warehouses", tables);
        Assert.Contains("sale_warehouses", tables);
        Assert.DoesNotContain(definition.Operations, operation => operation is not CreateModuleTable and not CreateModuleIndex);
    }
}
