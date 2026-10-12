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
}
