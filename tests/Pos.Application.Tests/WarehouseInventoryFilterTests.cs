using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class WarehouseInventoryFilterTests
{
    [Fact]
    public void Applies_warehouse_quantities_before_stock_filtering_and_keeps_catalog_fields()
    {
        var stockedId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        var items = new[]
        {
            new InventoryItemInfo(stockedId, "A", "Producto con existencia", 12m, "VES", 99m, true),
            new InventoryItemInfo(emptyId, "B", "Producto agotado", 20m, "USD", 88m, false)
        };
        var warehouseStock = new[] { new WarehouseStockClientInfo(stockedId, "A", "Producto con existencia", 3m, 12m, "VES") };

        var result = WarehouseInventoryFilter.Apply(items, warehouseStock, "available");

        var product = Assert.Single(result);
        Assert.Equal(stockedId, product.Id);
        Assert.Equal(3m, product.Quantity);
        Assert.Equal(12m, product.UnitPrice);
        Assert.True(product.HasImage);
    }

    [Fact]
    public void Out_of_stock_filter_uses_the_selected_warehouse_quantity()
    {
        var id = Guid.NewGuid();
        var item = new InventoryItemInfo(id, "A", "Producto", 12m, "VES", 10m);

        var result = WarehouseInventoryFilter.Apply([item], [], "out-of-stock");

        Assert.Equal(0m, Assert.Single(result).Quantity);
    }

    [Fact]
    public void Availability_filter_runs_against_the_quantity_replaced_by_the_warehouse_extension()
    {
        var id = Guid.NewGuid();
        var globalCatalog = new[] { new InventoryItemInfo(id, "A", "Producto", 12m, "VES", 10m) };
        var emptyWarehouse = new[] { new WarehouseStockClientInfo(id, "A", "Producto", 0m, 12m, "VES") };
        var stockedWarehouse = new[] { new WarehouseStockClientInfo(id, "A", "Producto", 4m, 12m, "VES") };

        var emptyLocationInventory = WarehouseInventoryFilter.Apply(globalCatalog, emptyWarehouse, string.Empty);
        var availableInStockedLocation = WarehouseInventoryFilter.Apply(globalCatalog, stockedWarehouse, string.Empty);

        var outOfStock = Assert.Single(InventoryStockFilter.Apply(emptyLocationInventory, "out-of-stock"));
        Assert.Equal(0m, outOfStock.Quantity);
        var available = Assert.Single(InventoryStockFilter.Apply(availableInStockedLocation, "available"));
        Assert.Equal(4m, available.Quantity);
        Assert.Empty(InventoryStockFilter.Apply(emptyLocationInventory, "available"));
    }

    [Fact]
    public void Without_a_warehouse_extension_availability_filter_keeps_using_global_stock()
    {
        var stocked = new InventoryItemInfo(Guid.NewGuid(), "A", "Con existencia", 12m, "VES", 10m);
        var empty = new InventoryItemInfo(Guid.NewGuid(), "B", "Agotado", 8m, "VES", 0m);

        var available = InventoryStockFilter.Apply([stocked, empty], "available");
        var outOfStock = InventoryStockFilter.Apply([stocked, empty], "out-of-stock");

        Assert.Equal(stocked.Id, Assert.Single(available).Id);
        Assert.Equal(empty.Id, Assert.Single(outOfStock).Id);
    }
}
