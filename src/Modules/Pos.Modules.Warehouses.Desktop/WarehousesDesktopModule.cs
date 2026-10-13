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
        registry.RegisterInventoryHistoryExtension(new WarehouseInventoryHistoryExtension());
        registry.RegisterReportSource(new PosDesktopReportSource("warehouses.stock", "Existencias por almacén", "Warehouse.View",
        [
            new("almacen", "Almacén", PosDesktopReportValueType.Text),
            new("sku", "Código", PosDesktopReportValueType.Text),
            new("producto", "Producto", PosDesktopReportValueType.Text),
            new("existencia", "Existencia", PosDesktopReportValueType.Decimal),
            new("precio", "Precio unitario", PosDesktopReportValueType.Decimal),
            new("moneda", "Moneda", PosDesktopReportValueType.Text)
        ], LoadWarehouseStock));
        registry.RegisterReportSource(new PosDesktopReportSource("warehouses.transfers", "Traslados", "Warehouse.Transfer",
        [
            new("fecha", "Fecha", PosDesktopReportValueType.DateTime),
            new("estado", "Estado", PosDesktopReportValueType.Text),
            new("origen", "Origen", PosDesktopReportValueType.Text),
            new("destino", "Destino", PosDesktopReportValueType.Text),
            new("productos", "Productos", PosDesktopReportValueType.Integer),
            new("unidades", "Unidades", PosDesktopReportValueType.Decimal),
            new("diferencia", "Diferencia recibida", PosDesktopReportValueType.Decimal)
        ], LoadWarehouseTransfers, SupportsDateRange: true));
    }

    private static async Task<PosDesktopReportResult> LoadWarehouseStock(PosDesktopModuleContext context,
        PosDesktopReportQuery query, CancellationToken cancellationToken)
    {
        var warehouses = await context.ApiClient.GetModuleAsync<List<WarehouseClientInfo>>(context.ModuleId, "warehouses", cancellationToken) ?? [];
        var selectedId = query.Filters.GetValueOrDefault("warehouseId");
        if (Guid.TryParse(selectedId, out var warehouseId)) warehouses = warehouses.Where(item => item.Id == warehouseId).ToList();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var warehouse in warehouses)
        {
            var stock = await context.ApiClient.GetModuleAsync<List<WarehouseStockClientInfo>>(context.ModuleId,
                $"warehouses/{warehouse.Id:D}/stock", cancellationToken) ?? [];
            rows.AddRange(stock.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["almacen"] = warehouse.Name, ["sku"] = item.Sku, ["producto"] = item.Name,
                ["existencia"] = item.Quantity, ["precio"] = item.UnitPrice, ["moneda"] = item.Currency
            }));
        }
        return new(rows);
    }

    private static async Task<PosDesktopReportResult> LoadWarehouseTransfers(PosDesktopModuleContext context,
        PosDesktopReportQuery query, CancellationToken cancellationToken)
    {
        var transfers = await context.ApiClient.GetModuleAsync<List<WarehouseTransferClientInfo>>(context.ModuleId, "transfers", cancellationToken) ?? [];
        var rows = transfers.Where(item => query.FromUtc is null || item.CreatedAtUtc >= query.FromUtc)
            .Where(item => query.ToUtc is null || item.CreatedAtUtc < query.ToUtc)
            .Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fecha"] = item.CreatedAtUtc, ["estado"] = item.StatusLabel, ["origen"] = item.Source,
                ["destino"] = item.Destination, ["productos"] = item.LineCount, ["unidades"] = item.Quantity,
                ["diferencia"] = item.Variance
            }).ToArray();
        return new(rows);
    }

    private sealed class WarehouseInventoryHistoryExtension : IPosInventoryHistoryExtension
    {
        public string RequiredPermission => "Warehouse.View";

        public async Task<ModuleInventoryMovementPageInfo> GetMovementsAsync(PosDesktopModuleContext context,
            Guid productId, int offset, int limit, CancellationToken cancellationToken = default)
        {
            var query = new Dictionary<string, string>
            {
                ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            return await context.ApiClient.GetModuleAsync<ModuleInventoryMovementPageInfo>(context.ModuleId,
                $"inventory/{productId:D}/movements", query, cancellationToken)
                ?? new ModuleInventoryMovementPageInfo([], false);
        }
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
            return WarehouseInventoryFilter.Apply(products, stock ?? [], string.Empty);
        }
    }
}
