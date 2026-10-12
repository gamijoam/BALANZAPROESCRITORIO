using System.Windows;
using System.Windows.Controls;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Modules.Warehouses.Desktop;

public sealed class WarehousesDesktopModule : IPosDesktopModule
{
    public string ModuleId => "warehouses";

    public void Configure(PosDesktopModuleRegistry registry)
    {
        registry.RegisterMenu("warehouses.main", "Almacenes", "Warehouse.View", context => new WarehouseWindow(context));
        registry.RegisterInventoryExtension(new WarehouseInventoryExtension());
    }

    private sealed class WarehouseInventoryExtension : IPosInventoryScreenExtension
    {
        private Guid? _selectedWarehouseId;
        public string RequiredPermission => "Warehouse.View";

        public FrameworkElement CreateFilterControl(PosDesktopModuleContext context, Func<Task> refreshInventory)
        {
            var selector = new ComboBox { Width = 190, Height = 34, Margin = new Thickness(7, 0, 0, 0), DisplayMemberPath = nameof(WarehouseClientInfo.Name), ToolTip = "Filtrar existencias por almacén" };
            selector.Items.Add(new WarehouseClientInfo(Guid.Empty, "", "Almacén: todos", true, false, 0m));
            selector.SelectedIndex = 0;
            selector.SelectionChanged += async (_, _) =>
            {
                _selectedWarehouseId = selector.SelectedItem is WarehouseClientInfo { Id: var id } && id != Guid.Empty ? id : null;
                await refreshInventory();
            };
            selector.Loaded += async (_, _) =>
            {
                try
                {
                    var warehouses = await context.ApiClient.GetModuleAsync<List<WarehouseClientInfo>>(context.ModuleId, "warehouses");
                    foreach (var warehouse in warehouses ?? []) selector.Items.Add(warehouse);
                }
                catch { /* Inventory remains usable if the optional module endpoint is unavailable. */ }
            };
            return selector;
        }

        public async Task<IReadOnlyList<InventoryItemInfo>> ApplyFilterAsync(PosDesktopModuleContext context,
            IReadOnlyList<InventoryItemInfo> products, CancellationToken cancellationToken = default)
        {
            if (_selectedWarehouseId is not Guid warehouseId) return products;
            var stock = await context.ApiClient.GetModuleAsync<List<WarehouseStockClientInfo>>(context.ModuleId,
                $"warehouses/{warehouseId:D}/stock", new Dictionary<string, string> { ["includeInactive"] = "true" }, cancellationToken);
            var ids = (stock ?? []).Select(item => item.ProductId).ToHashSet();
            return products.Where(product => ids.Contains(product.Id)).ToArray();
        }
    }
}
