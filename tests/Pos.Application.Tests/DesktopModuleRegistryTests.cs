using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using System.Windows;

namespace Pos.Application.Tests;

public sealed class DesktopModuleRegistryTests
{
    [Fact]
    public void RegisterMenu_RequiresPermissionDeclaredByTheModule()
    {
        var registry = new PosDesktopModuleRegistry("warehouses", ["Warehouse.View"]);

        var error = Assert.Throws<InvalidOperationException>(() => registry.RegisterMenu(
            "transfers", "Traslados", "Warehouse.Manage", _ => new Window()));

        Assert.Contains("debe estar declarado", error.Message, StringComparison.Ordinal);
        Assert.Empty(registry.Menus);
    }

    [Fact]
    public void RegisterMenu_RejectsDuplicateIdsAndKeepsDeclaredPermission()
    {
        var registry = new PosDesktopModuleRegistry("warehouses", ["Warehouse.View"]);
        registry.RegisterMenu("stock", "Almacenes", "Warehouse.View", _ => new Window());

        Assert.Throws<InvalidOperationException>(() => registry.RegisterMenu(
            "STOCK", "Existencias", "Warehouse.View", _ => new Window()));
        var menu = Assert.Single(registry.Menus);
        Assert.Equal("Warehouse.View", menu.RequiredPermission);
    }

    [Fact]
    public void RegisterInventoryExtension_AllowsOnlyOneContributionPerModule()
    {
        var registry = new PosDesktopModuleRegistry("warehouses", ["Warehouse.View"]);
        registry.RegisterInventoryExtension(new TestInventoryExtension());

        Assert.Throws<InvalidOperationException>(() => registry.RegisterInventoryExtension(new TestInventoryExtension()));
        Assert.Single(registry.InventoryExtensions);
    }

    [Fact]
    public void RegisterReceiptExtension_RequiresDeclaredPrintPermissionAndAllowsOnlyOne()
    {
        var missingPermission = new PosDesktopModuleRegistry("thermal-receipts", []);
        Assert.Throws<InvalidOperationException>(() => missingPermission.RegisterReceiptExtension("ThermalReceipts.Print", new TestReceiptExtension()));

        var registry = new PosDesktopModuleRegistry("thermal-receipts", ["ThermalReceipts.Print"]);
        registry.RegisterReceiptExtension("ThermalReceipts.Print", new TestReceiptExtension());
        Assert.Equal("ThermalReceipts.Print", registry.ReceiptExtension!.RequiredPermission);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterReceiptExtension("ThermalReceipts.Print", new TestReceiptExtension()));
    }

    private sealed class TestInventoryExtension : IPosInventoryScreenExtension
    {
        public string RequiredPermission => "Warehouse.View";
        public FrameworkElement CreateFilterControl(PosDesktopModuleContext context, Func<Task> refreshInventory) => null!;
        public Task<IReadOnlyList<InventoryItemInfo>> ApplyFilterAsync(PosDesktopModuleContext context,
            IReadOnlyList<InventoryItemInfo> products, CancellationToken cancellationToken = default) => Task.FromResult(products);
    }

    private sealed class TestReceiptExtension : IPosReceiptExtension
    {
        public Task<PosDesktopReceiptPrintResult> PrintAsync(PosDesktopModuleContext context, SaleReceipt receipt,
            CancellationToken cancellationToken = default) => Task.FromResult(new PosDesktopReceiptPrintResult(true));
        public Window CreatePreviewWindow(PosDesktopModuleContext context, SaleReceipt receipt) => null!;
    }
}
