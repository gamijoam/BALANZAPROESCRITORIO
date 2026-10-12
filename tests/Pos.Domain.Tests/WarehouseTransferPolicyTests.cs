using Pos.Domain.Inventory;
using Pos.Domain.Identity;

namespace Pos.Domain.Tests;

public sealed class WarehouseTransferPolicyTests
{
    [Fact]
    public void Dispatch_reduces_source_and_keeps_quantity_in_transit()
    {
        var result = WarehouseTransferPolicy.Dispatch("Draft", 8m, 3m);

        Assert.Equal("Dispatched", result.Status);
        Assert.Equal(-3m, result.SourceDelta);
        Assert.Equal(3m, result.InTransitDelta);
        Assert.Equal(0m, result.DestinationDelta);
    }

    [Fact]
    public void Receive_moves_dispatched_quantity_into_destination()
    {
        var result = WarehouseTransferPolicy.Receive("Dispatched", 3m, 3m);

        Assert.Equal("Received", result.Status);
        Assert.Equal(0m, result.SourceDelta);
        Assert.Equal(-3m, result.InTransitDelta);
        Assert.Equal(3m, result.DestinationDelta);
    }

    [Fact]
    public void Receive_records_a_shortage_as_auditable_variance()
    {
        var result = WarehouseTransferPolicy.Receive("Dispatched", 3m, 2m);

        Assert.Equal("Received", result.Status);
        Assert.Equal(-3m, result.InTransitDelta);
        Assert.Equal(2m, result.DestinationDelta);
        Assert.Equal(-1m, result.Variance);
    }

    [Fact]
    public void Dispatch_rejects_insufficient_stock_or_invalid_state()
    {
        Assert.Throws<ArgumentException>(() => WarehouseTransferPolicy.Dispatch("Draft", 2m, 3m));
        Assert.Throws<ArgumentException>(() => WarehouseTransferPolicy.Dispatch("Received", 8m, 3m));
    }

    [Theory]
    [InlineData(Pos.Domain.Identity.Permission.ViewWarehouses)]
    [InlineData(Pos.Domain.Identity.Permission.ManageWarehouses)]
    [InlineData(Pos.Domain.Identity.Permission.TransferInventory)]
    public void Warehouse_permissions_are_supervisor_operations(Permission permission)
    {
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(OperatorRole.Supervisor, permission, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(OperatorRole.Cashier, permission, PermissionOverrideMode.Inherit));
    }
}
