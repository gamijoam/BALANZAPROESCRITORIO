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
        Assert.Equal(new Version(1, 3, 3), manifest.Version);
        Assert.Equal(new Version(1, 1, 0), manifest.DesktopApi!.Min);
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

    [Fact]
    public void Transfer_history_endpoint_is_read_only_and_requires_warehouse_view()
    {
        var endpoint = Assert.Single(new WarehouseModule().GetEndpoints(), item =>
            item.Method == "GET" && item.Route == "inventory/{productId:guid}/movements");

        Assert.Equal("Warehouse.View", endpoint.RequiredPermission);
    }

    [Theory]
    [InlineData("Draft", "transfer.dispatched", "5", "", "")]
    [InlineData("Received", "transfer.dispatched", "5", "", "-5")]
    [InlineData("Received", "transfer.received", "5", "4", "4")]
    [InlineData("Received", "transfer.received", "5", "0", "")]
    [InlineData("Draft", "transfer.received", "5", "5", "")]
    public void Transfer_history_uses_actual_dispatch_and_receipt_quantities(
        string status, string action, string sent, string received, string expectedDelta)
    {
        var delta = WarehouseTransferMovementProjector.GetQuantityDelta(status, action, decimal.Parse(sent),
            string.IsNullOrEmpty(received) ? null : decimal.Parse(received));

        Assert.Equal(string.IsNullOrEmpty(expectedDelta) ? null : decimal.Parse(expectedDelta), delta);
    }
}
