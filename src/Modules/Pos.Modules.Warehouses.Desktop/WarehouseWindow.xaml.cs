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
    private IReadOnlyList<WarehouseStockClientInfo> _transferSourceStock = [];
    private Guid? _transferSourceStockWarehouseId;
    private ProductInfo? _selectedTransferProduct;
    private bool _loadingWarehouses;
    private int _transferDetailsRequestId;

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
        TransferDetailsGrid.ItemsSource = Array.Empty<WarehouseTransferDetailLineClientInfo>();
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn { Header = "Código", Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Sku)), Width = 130 });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn { Header = "Producto", Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Existencia",
            Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Quantity)) { Converter = WarehouseQuantityConverter.Instance },
            Width = 110
        });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Precio",
            Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.UnitPrice)) { Converter = WarehouseAmountConverter.Instance },
            Width = 120
        });
        WarehouseStockGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Moneda",
            Binding = new System.Windows.Data.Binding(nameof(WarehouseStockClientInfo.Currency)),
            Width = 80
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadWarehousesAsync();
        TransferProductBox.Focus();
    }

    private async Task LoadWarehousesAsync(Guid? selectedId = null)
    {
        try
        {
            _loadingWarehouses = true;
            var warehouses = await _context.ApiClient.GetModuleAsync<List<WarehouseClientInfo>>(_context.ModuleId, "warehouses") ?? [];
            WarehousePicker.ItemsSource = warehouses;
            TransferSourcePicker.ItemsSource = warehouses.Where(x => x.IsActive).ToArray();
            TransferDestinationPicker.ItemsSource = warehouses.Where(x => x.IsActive).ToArray();
            var selected = warehouses.FirstOrDefault(x => x.Id == selectedId) ?? warehouses.FirstOrDefault(x => x.IsPrimary);
            WarehousePicker.SelectedItem = selected;
            DeactivateWarehouseButton.IsEnabled = selected is { IsActive: true, IsPrimary: false };
            TransferSourcePicker.SelectedItem = selected;
            TransferDestinationPicker.SelectedItem = warehouses.FirstOrDefault(x => x.IsActive && x.Id != selected?.Id);
            _loadingWarehouses = false;

            if (selected is not null)
            {
                await LoadStockAsync(selected.Id);
                await LoadTransferSourceStockAsync(selected.Id);
            }
            await LoadTransfersAsync();
            if (warehouses.Count == 1)
                SetStatus("Solo existe el almacén principal. Crea otro para comenzar a usar traslados.");
            else if (string.IsNullOrWhiteSpace(WarehouseStatus.Text) || WarehouseStatus.Text == "Preparado.")
                SetStatus($"{warehouses.Count} almacenes registrados.");
        }
        catch (Exception exception)
        {
            _loadingWarehouses = false;
            ShowError(exception);
        }
    }

    private async Task LoadStockAsync(Guid warehouseId)
    {
        try
        {
            WarehouseStockGrid.ItemsSource = await _context.ApiClient.GetModuleAsync<List<WarehouseStockClientInfo>>(
                _context.ModuleId, $"warehouses/{warehouseId:D}/stock") ?? [];
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task LoadTransferSourceStockAsync(Guid warehouseId)
    {
        try
        {
            var stock = await _context.ApiClient.GetModuleAsync<List<WarehouseStockClientInfo>>(
                _context.ModuleId, $"warehouses/{warehouseId:D}/stock") ?? [];
            if (TransferSourcePicker.SelectedItem is not WarehouseClientInfo selected || selected.Id != warehouseId) return;
            _transferSourceStock = stock;
            _transferSourceStockWarehouseId = warehouseId;
            UpdateSelectedTransferProductSummary();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task LoadTransfersAsync(Guid? preferredTransferId = null)
    {
        try
        {
            var previousId = (TransfersGrid.SelectedItem as WarehouseTransferClientInfo)?.Id;
            var transfers = await _context.ApiClient.GetModuleAsync<List<WarehouseTransferClientInfo>>(_context.ModuleId, "transfers") ?? [];
            TransfersGrid.ItemsSource = transfers;
            TransfersGrid.SelectedItem = transfers.FirstOrDefault(item => item.Id == preferredTransferId)
                ?? transfers.FirstOrDefault(item => item.Id == previousId)
                ?? transfers.FirstOrDefault();
            if (TransfersGrid.SelectedItem is null)
            {
                SelectedTransferSummary.Text = "Todavía no hay traslados registrados.";
                TransferDetailsGrid.ItemsSource = Array.Empty<WarehouseTransferDetailLineClientInfo>();
                UpdateTransferActions(null);
            }
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void WarehousePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WarehousePicker.SelectedItem is WarehouseClientInfo warehouse) await LoadStockAsync(warehouse.Id);
    }

    private async void TransferSourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingWarehouses) return;
        if (TransferSourcePicker.SelectedItem is WarehouseClientInfo warehouse)
        {
            if (TransferDestinationPicker.SelectedItem is WarehouseClientInfo destination && destination.Id == warehouse.Id)
                TransferDestinationPicker.SelectedItem = (TransferDestinationPicker.ItemsSource as IEnumerable<WarehouseClientInfo>)?.FirstOrDefault(item => item.Id != warehouse.Id);
            if (_transferSourceStockWarehouseId != warehouse.Id)
            {
                _transferSourceStock = [];
                _transferSourceStockWarehouseId = null;
                UpdateSelectedTransferProductSummary();
            }
            await LoadTransferSourceStockAsync(warehouse.Id);
        }
        else
        {
            _transferSourceStock = [];
            _transferSourceStockWarehouseId = null;
            UpdateSelectedTransferProductSummary();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadWarehousesAsync((WarehousePicker.SelectedItem as WarehouseClientInfo)?.Id);
    private async void RefreshTransfers_Click(object sender, RoutedEventArgs e) => await LoadTransfersAsync();

    private async void CreateWarehouse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _context.ApiClient.PostModuleAsync<CreateWarehouseRequest>(_context.ModuleId, "warehouses", new(WarehouseCodeBox.Text, WarehouseNameBox.Text));
            WarehouseCodeBox.Clear();
            WarehouseNameBox.Clear();
            SetStatus("Almacén creado.");
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
        try
        {
            await _context.ApiClient.PatchModuleAsync<SetWarehouseActiveRequest>(_context.ModuleId, $"warehouses/{warehouse.Id:D}/active", new(false));
            SetStatus("Almacén desactivado.");
            await LoadWarehousesAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void AssignTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (WarehousePicker.SelectedItem is not WarehouseClientInfo warehouse) return;
        try
        {
            await _context.ApiClient.PutModuleAsync<AssignTerminalRequest>(_context.ModuleId, $"terminals/{_context.TerminalId:D}/warehouse", new(warehouse.Id));
            SetStatus($"Esta terminal quedó asignada a {warehouse.Name}. Las ventas descontarán de este almacén.");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void TransferProductBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchTransferProductsAsync();
    }

    private async void BuscarTransferProducts_Click(object sender, RoutedEventArgs e) => await SearchTransferProductsAsync();

    private async Task SearchTransferProductsAsync()
    {
        var query = TransferProductBox.Text.Trim();
        if (query.Length == 0)
        {
            SetStatus("Escribe el código o nombre del producto que quieres trasladar.");
            TransferProductBox.Focus();
            return;
        }

        ClearSelectedTransferProduct();
        ProductSearchResults.ItemsSource = null;
        ProductSearchGroup.Visibility = Visibility.Collapsed;
        try
        {
            var products = await _context.SearchProductsAsync(query, CancellationToken.None);
            if (products.Count == 0)
            {
                SetStatus("No se encontraron productos. Revisa el código o prueba con otro nombre.");
                return;
            }

            ProductSearchResults.ItemsSource = products;
            ProductSearchGroup.Visibility = Visibility.Visible;
            SetStatus($"Se encontraron {products.Count} productos. Selecciona el artículo correcto antes de agregarlo.");
            ProductSearchResults.Focus();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ProductSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductSearchResults.SelectedItem is ProductInfo product) SelectTransferProduct(product);
    }

    private void ProductSearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ProductSearchResults.SelectedItem is not ProductInfo product) return;
        e.Handled = true;
        SelectTransferProduct(product);
        TransferQuantityBox.Focus();
        TransferQuantityBox.SelectAll();
    }

    private async void TransferQuantityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddTransferLineAsync();
    }

    private void SelectTransferProduct(ProductInfo product)
    {
        _selectedTransferProduct = product;
        UpdateSelectedTransferProductSummary();
    }

    private void UpdateSelectedTransferProductSummary()
    {
        if (_selectedTransferProduct is not ProductInfo product)
        {
            SelectedTransferProductName.Text = "Busca y selecciona un producto";
            SelectedTransferProductSku.Text = "Código: —";
            SelectedTransferProductSourceStock.Text = TransferSourcePicker.SelectedItem is WarehouseClientInfo
                ? "Selecciona un producto"
                : "Selecciona el almacén de origen";
            AddTransferLineButton.IsEnabled = false;
            return;
        }

        SelectedTransferProductName.Text = product.Name;
        SelectedTransferProductSku.Text = $"Código: {product.Sku}";
        if (TransferSourcePicker.SelectedItem is not WarehouseClientInfo source)
            SelectedTransferProductSourceStock.Text = "Selecciona el almacén de origen";
        else if (_transferSourceStockWarehouseId != source.Id)
            SelectedTransferProductSourceStock.Text = "Consultando existencia…";
        else
        {
            var stock = _transferSourceStock.FirstOrDefault(item => item.ProductId == product.Id);
            SelectedTransferProductSourceStock.Text = $"{PosNumberFormat.FormatQuantity(stock?.Quantity ?? 0m)} unidades";
        }
        AddTransferLineButton.IsEnabled = TransferSourcePicker.SelectedItem is WarehouseClientInfo activeSource
            && _transferSourceStockWarehouseId == activeSource.Id;
    }

    private void ClearSelectedTransferProduct()
    {
        _selectedTransferProduct = null;
        UpdateSelectedTransferProductSummary();
    }

    private async void AddTransferLine_Click(object sender, RoutedEventArgs e) => await AddTransferLineAsync();

    private Task AddTransferLineAsync()
    {
        if (_selectedTransferProduct is not ProductInfo product)
        {
            SetStatus("Busca y selecciona un producto antes de agregarlo al traslado.");
            TransferProductBox.Focus();
            return Task.CompletedTask;
        }
        if (!decimal.TryParse(TransferQuantityBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) || quantity <= 0m || decimal.Round(quantity, 3) != quantity)
        {
            SetStatus("La cantidad debe ser positiva y tener hasta tres decimales.");
            TransferQuantityBox.Focus();
            TransferQuantityBox.SelectAll();
            return Task.CompletedTask;
        }

        var current = _draftLines.FirstOrDefault(x => x.ProductId == product.Id);
        if (current is null) _draftLines.Add(new(product.Id, product.Sku, product.Name, quantity));
        else current.Quantity += quantity;
        TransferDraftGrid.Items.Refresh();
        TransferProductBox.Clear();
        TransferQuantityBox.Text = "1";
        ProductSearchResults.ItemsSource = null;
        ProductSearchGroup.Visibility = Visibility.Collapsed;
        ClearSelectedTransferProduct();
        SetStatus($"{product.Name} agregado al borrador.");
        TransferProductBox.Focus();
        return Task.CompletedTask;
    }

    private async void CreateTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransferSourcePicker.SelectedItem is not WarehouseClientInfo source || TransferDestinationPicker.SelectedItem is not WarehouseClientInfo destination || _draftLines.Count == 0)
        {
            SetStatus("Selecciona el origen, el destino y al menos un producto.");
            return;
        }
        if (source.Id == destination.Id)
        {
            SetStatus("El almacén de origen y el de destino deben ser distintos.");
            return;
        }
        try
        {
            var created = await _context.ApiClient.PostModuleAsync<CreateTransferRequest, CreatedTransferResponse>(_context.ModuleId, "transfers",
                new(source.Id, destination.Id, _draftLines.Select(x => new TransferLineRequest(x.ProductId, x.Quantity)).ToArray()));
            _draftLines.Clear();
            TransferDraftGrid.Items.Refresh();
            SetStatus("Traslado creado como borrador. Revísalo en Traslados recientes y despáchalo cuando salga del origen.");
            TransferHistoryTab.IsSelected = true;
            await LoadTransfersAsync(created?.Id);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void TransfersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadSelectedTransferDetailsAsync();

    private async Task LoadSelectedTransferDetailsAsync()
    {
        var requestId = ++_transferDetailsRequestId;
        if (TransfersGrid.SelectedItem is not WarehouseTransferClientInfo transfer)
        {
            SelectedTransferSummary.Text = "Selecciona un traslado de la lista.";
            TransferDetailsGrid.ItemsSource = Array.Empty<WarehouseTransferDetailLineClientInfo>();
            UpdateTransferActions(null);
            return;
        }

        SelectedTransferSummary.Text = $"{transfer.Source} → {transfer.Destination} · {transfer.StatusLabel} · {transfer.CreatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
        TransferDetailsGrid.ItemsSource = Array.Empty<WarehouseTransferDetailLineClientInfo>();
        UpdateTransferActions(transfer);
        try
        {
            var detail = await _context.ApiClient.GetModuleAsync<WarehouseTransferDetailsClientInfo>(
                _context.ModuleId, $"transfers/{transfer.Id:D}");
            if (requestId != _transferDetailsRequestId || TransfersGrid.SelectedItem is not WarehouseTransferClientInfo selected || selected.Id != transfer.Id) return;
            TransferDetailsGrid.ItemsSource = detail?.Lines ?? [];
        }
        catch (Exception exception)
        {
            if (requestId == _transferDetailsRequestId) ShowError(exception);
        }
    }

    private void UpdateTransferActions(WarehouseTransferClientInfo? transfer)
    {
        DispatchTransferButton.IsEnabled = transfer?.Status == "Draft";
        ReceiveTransferButton.IsEnabled = transfer?.Status == "Dispatched";
    }

    private async void DispatchTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not WarehouseTransferClientInfo { Status: "Draft" } transfer) return;
        DispatchTransferButton.IsEnabled = false;
        try
        {
            await _context.ApiClient.PostModuleAsync<object>(_context.ModuleId, $"transfers/{transfer.Id:D}/dispatch", new { });
            SetStatus("Traslado despachado. Las unidades salieron del origen y están en tránsito.");
            await LoadTransfersAsync();
            if (TransferSourcePicker.SelectedItem is WarehouseClientInfo source && source.Id == transfer.SourceWarehouseId)
                await LoadTransferSourceStockAsync(source.Id);
            await LoadStockAsync(transfer.SourceWarehouseId);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void ReceiveTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransfersGrid.SelectedItem is not WarehouseTransferClientInfo { Status: "Dispatched" } transfer) return;
        var answer = MessageBox.Show(this, "¿Confirmas que recibiste todas las unidades enviadas? La diferencia real se registra en el historial.",
            "Recibir traslado", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        ReceiveTransferButton.IsEnabled = false;
        try
        {
            await _context.ApiClient.PostModuleAsync<ReceiveTransferRequest>(_context.ModuleId,
                $"transfers/{transfer.Id:D}/receive", new([]));
            SetStatus("Traslado recibido en el almacén de destino.");
            await LoadTransfersAsync();
            if (TransferSourcePicker.SelectedItem is WarehouseClientInfo source && source.Id == transfer.DestinationWarehouseId)
                await LoadTransferSourceStockAsync(source.Id);
            await LoadStockAsync(transfer.DestinationWarehouseId);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void SetStatus(string message) => WarehouseStatus.Text = message;

    private void ShowError(Exception exception)
    {
        var message = PosUserMessage.ForException(exception);
        SetStatus(message);
        if (message.Contains("404", StringComparison.OrdinalIgnoreCase) || message.Contains("no está activo", StringComparison.OrdinalIgnoreCase))
            SetStatus(message + " Instala o activa el módulo desde Administración → Módulos y reinicia el servicio.");
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
    private sealed record CreatedTransferResponse(Guid Id, string Status);
    private sealed record TransferLineRequest(Guid ProductId, decimal Quantity);
    private sealed record ReceiveTransferRequest(IReadOnlyList<WarehouseReceiptClientLine> Lines);
}

public sealed record WarehouseTransferDetailsClientInfo(Guid Id, string Status, Guid SourceWarehouseId, string Source,
    Guid DestinationWarehouseId, string Destination, DateTime CreatedAtUtc, IReadOnlyList<WarehouseTransferDetailLineClientInfo> Lines);
public sealed record WarehouseTransferDetailLineClientInfo(Guid ProductId, string Sku, string Name, decimal Quantity,
    decimal? ReceivedQuantity, decimal Variance);

public sealed class WarehouseQuantityConverter : System.Windows.Data.IValueConverter
{
    public static WarehouseQuantityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        decimal quantity => PosNumberFormat.FormatQuantity(quantity),
        null => "—",
        _ => value.ToString() ?? "—"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class WarehouseAmountConverter : System.Windows.Data.IValueConverter
{
    public static WarehouseAmountConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is decimal amount
        ? PosNumberFormat.FormatAmount(amount)
        : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
