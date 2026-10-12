using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Modules.Warehouses.Desktop;

public partial class WarehouseWindow : Window
{
    private readonly PosDesktopModuleContext _context;
    private readonly ObservableCollection<WarehouseDraftLine> _draftLines = [];

    public WarehouseWindow(PosDesktopModuleContext context)
    {
        InitializeComponent();
        _context = context;
        var canManage = context.GrantedPermissions.Contains("Warehouse.Manage");
        var canTransfer = context.GrantedPermissions.Contains("Warehouse.Transfer");
        AssignTerminalButton.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
        DeactivateWarehouseButton.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
        CreateWarehouseGroup.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
        TransfersTab.Visibility = canTransfer ? Visibility.Visible : Visibility.Collapsed;
        TransferDraftGrid.ItemsSource = _draftLines;
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn { Header = "Código", Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Sku)), Width = 130 });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn { Header = "Producto", Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn { Header = "Existencia", Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Quantity)), Width = 110 });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn { Header = "Precio", Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.UnitPrice)), Width = 120 });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadWarehousesAsync();

    private async Task LoadWarehousesAsync(Guid? selectedId = null)
    {
        try
        {
            var warehouses = await _context.ApiClient.GetModuleAsync<List<WarehouseClientInfo>>(_context.ModuleId, "warehouses") ?? [];
            WarehousePicker.ItemsSource = warehouses;
            TransferSourcePicker.ItemsSource = warehouses.Where(x => x.IsActive).ToArray();
            TransferDestinationPicker.ItemsSource = warehouses.Where(x => x.IsActive).ToArray();
            var selected = warehouses.FirstOrDefault(x => x.Id == selectedId) ?? warehouses.FirstOrDefault(x => x.IsPrimary);
            WarehousePicker.SelectedItem = selected;
            DeactivateWarehouseButton.IsEnabled = selected is { IsActive: true, IsPrimary: false };
            TransferSourcePicker.SelectedItem = selected;
            TransferDestinationPicker.SelectedItem = warehouses.FirstOrDefault(x => x.IsActive && x.Id != selected?.Id);
            if (selected is not null) await LoadStockAsync(selected.Id);
            await LoadTransfersAsync();
            WarehouseStatus.Text = warehouses.Count == 1 ? "Solo existe el almacén principal. Crea otro para comenzar a usar traslados." : $"{warehouses.Count} almacenes registrados.";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task LoadStockAsync(Guid warehouseId)
    {
        try { WarehouseStockGrid.ItemsSource = await _context.ApiClient.GetModuleAsync<List<WarehouseStockClientInfo>>(_context.ModuleId, $"warehouses/{warehouseId:D}/stock") ?? []; }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task LoadTransfersAsync()
    {
        try { TransfersGrid.ItemsSource = await _context.ApiClient.GetModuleAsync<List<WarehouseTransferClientInfo>>(_context.ModuleId, "transfers") ?? []; }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void WarehousePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WarehousePicker.SelectedItem is WarehouseClientInfo warehouse) await LoadStockAsync(warehouse.Id);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadWarehousesAsync((WarehousePicker.SelectedItem as WarehouseClientInfo)?.Id);
    private async void RefreshTransfers_Click(object sender, RoutedEventArgs e) => await LoadTransfersAsync();

    private async void CreateWarehouse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _context.ApiClient.PostModuleAsync<CreateWarehouseRequest>(_context.ModuleId, "warehouses", new(WarehouseCodeBox.Text, WarehouseNameBox.Text));
            WarehouseCodeBox.Clear(); WarehouseNameBox.Clear();
            WarehouseStatus.Text = "Almacén creado.";
            await LoadWarehousesAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void DeactivateWarehouse_Click(object sender, RoutedEventArgs e)
    {
        if (WarehousePicker.SelectedItem is not WarehouseClientInfo { IsPrimary: false, IsActive: true } warehouse) return;
        var answer = MessageBox.Show(this, $"¿Desactivar el almacén {warehouse.Name}? El servidor lo rechazará si aún tiene existencias o traslados abiertos.",
            "Desactivar almacén", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try { await _context.ApiClient.PatchModuleAsync<SetWarehouseActiveRequest>(_context.ModuleId, $"warehouses/{warehouse.Id:D}/active", new(false)); WarehouseStatus.Text = "Almacén desactivado."; await LoadWarehousesAsync(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void AssignTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (WarehousePicker.SelectedItem is not WarehouseClientInfo warehouse) return;
        try
        {
            await _context.ApiClient.PutModuleAsync<AssignTerminalRequest>(_context.ModuleId, $"terminals/{_context.TerminalId:D}/warehouse", new(warehouse.Id));
            WarehouseStatus.Text = $"Esta terminal quedó asignada a {warehouse.Name}. Las ventas descontarán de este almacén.";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void TransferProductBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddTransferLineAsync();
    }

    private async void AddTransferLine_Click(object sender, RoutedEventArgs e) => await AddTransferLineAsync();

    private async Task AddTransferLineAsync()
    {
        var query = TransferProductBox.Text.Trim();
        if (query.Length == 0) return;
        if (!decimal.TryParse(TransferQuantityBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) || quantity <= 0m || decimal.Round(quantity, 3) != quantity)
        { WarehouseStatus.Text = "La cantidad debe ser positiva y tener hasta tres decimales."; return; }
        try
        {
            var products = await _context.SearchProductsAsync(query, CancellationToken.None);
            var product = products.FirstOrDefault(x => string.Equals(x.Sku, query, StringComparison.OrdinalIgnoreCase)) ?? products.FirstOrDefault();
            if (product is null) { WarehouseStatus.Text = "No se encontró el producto."; return; }
            var current = _draftLines.FirstOrDefault(x => x.ProductId == product.Id);
            if (current is null) _draftLines.Add(new(product.Id, product.Sku, product.Name, quantity));
            else current.Quantity += quantity;
            TransferDraftGrid.Items.Refresh();
            TransferProductBox.Clear(); TransferQuantityBox.Text = "1"; TransferProductBox.Focus();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void CreateTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransferSourcePicker.SelectedItem is not WarehouseClientInfo source || TransferDestinationPicker.SelectedItem is not WarehouseClientInfo destination || _draftLines.Count == 0)
        { WarehouseStatus.Text = "Selecciona el origen, destino y al menos un producto."; return; }
        try
        {
            await _context.ApiClient.PostModuleAsync<CreateTransferRequest>(_context.ModuleId, "transfers", new(source.Id, destination.Id, _draftLines.Select(x => new TransferLineRequest(x.ProductId, x.Quantity)).ToArray()));
            _draftLines.Clear();
            WarehouseStatus.Text = "Traslado creado como borrador. Despáchalo cuando el inventario salga del origen.";
            await LoadTransfersAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void DispatchTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not WarehouseTransferClientInfo { Status: "Draft" } transfer) { WarehouseStatus.Text = "Selecciona un traslado en estado Draft."; return; }
        try { await _context.ApiClient.PostModuleAsync<object>(_context.ModuleId, $"transfers/{transfer.Id:D}/dispatch", new { }); WarehouseStatus.Text = "Traslado despachado; las unidades quedaron en tránsito."; await LoadTransfersAsync(); await LoadWarehousesAsync(transfer.SourceWarehouseId); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void ReceiveTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not WarehouseTransferClientInfo { Status: "Dispatched" } transfer) { WarehouseStatus.Text = "Selecciona un traslado despachado."; return; }
        var answer = MessageBox.Show(this, "¿Confirmas que recibiste todas las unidades enviadas? La diferencia real se registra en el historial.", "Recibir traslado", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try { await _context.ApiClient.PostModuleAsync<ReceiveTransferRequest>(_context.ModuleId, $"transfers/{transfer.Id:D}/receive", new([])); WarehouseStatus.Text = "Traslado recibido en el almacén de destino."; await LoadTransfersAsync(); await LoadWarehousesAsync(transfer.DestinationWarehouseId); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception)
    {
        var message = PosUserMessage.ForException(exception);
        WarehouseStatus.Text = message;
        if (message.Contains("404", StringComparison.OrdinalIgnoreCase) || message.Contains("no está activo", StringComparison.OrdinalIgnoreCase))
            WarehouseStatus.Text += " Instala o activa el módulo desde Administración → Módulos y reinicia el servicio.";
    }

    private sealed class WarehouseDraftLine(Guid productId, string sku, string name, decimal quantity)
    {
        public Guid ProductId { get; } = productId;
        public string Sku { get; } = sku;
        public string Name { get; } = name;
        public decimal Quantity { get; set; } = quantity;
    }

    private sealed record CreateWarehouseRequest(string Code, string Name);
    private sealed record SetWarehouseActiveRequest(bool Active);
    private sealed record AssignTerminalRequest(Guid WarehouseId);
    private sealed record CreateTransferRequest(Guid SourceWarehouseId, Guid DestinationWarehouseId, IReadOnlyList<TransferLineRequest> Lines);
    private sealed record TransferLineRequest(Guid ProductId, decimal Quantity);
    private sealed record ReceiveTransferRequest(IReadOnlyList<WarehouseReceiptClientLine> Lines);
}
