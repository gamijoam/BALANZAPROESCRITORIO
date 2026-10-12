namespace Pos.Desktop.Presentation;

/// <summary>Applies location-specific stock to catalog rows and filters using that location's quantity.</summary>
public static class WarehouseInventoryFilter
{
    public static IReadOnlyList<InventoryItemInfo> Apply(
        IReadOnlyList<InventoryItemInfo> catalog,
        IReadOnlyList<WarehouseStockClientInfo> warehouseStock,
        string stockFilter)
    {
        var quantities = warehouseStock.ToDictionary(item => item.ProductId, item => item.Quantity);
        var items = catalog.Select(item => item with
        {
            Quantity = quantities.GetValueOrDefault(item.Id)
        });

        return stockFilter switch
        {
            "available" => items.Where(item => item.Quantity > 0m).ToArray(),
            "out-of-stock" => items.Where(item => item.Quantity <= 0m).ToArray(),
            _ => items.ToArray()
        };
    }
}
