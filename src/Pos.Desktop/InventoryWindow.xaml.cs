using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class InventoryWindow : Window
{
    private readonly PosViewModel _viewModel;
    private int _imageLoadVersion;
    private readonly IReadOnlyList<LoadedDesktopModule> _modules;
    private bool _extensionsCreated;
    internal InventoryWindow(PosViewModel viewModel, IReadOnlyList<LoadedDesktopModule>? modules = null)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = workArea.Width - 16;
        Height = workArea.Height - 16;
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        _viewModel = viewModel;
        _modules = modules ?? [];
        Loaded += async (_, _) =>
        {
            CreateInventoryExtensions();
            await LoadAsync();
        };
    }

    private void CreateInventoryExtensions()
    {
        if (_extensionsCreated) return;
        _extensionsCreated = true;
        foreach (var module in _modules)
        foreach (var extension in module.Registry.InventoryExtensions)
        {
            if (!module.Context.GrantedPermissions.Contains(extension.RequiredPermission)) continue;
            try { InventoryExtensionsPanel.Children.Add(extension.CreateFilterControl(module.Context, () => LoadAsync())); }
            catch (Exception exception) { StatusLabel.Text = $"No se cargó un filtro de módulo: {PosUserMessage.ForException(exception)}"; }
        }
    }

    private async Task LoadAsync(Guid? selectedProductId = null)
    {
        try
        {
            var status = StatusFilter.SelectedIndex switch { 1 => "inactive", 2 => "all", _ => "active" };
            var currency = CurrencyFilter.SelectedIndex switch { 1 => "VES", 2 => "USD", _ => "" };
            var stock = StockFilter.SelectedIndex switch { 1 => "available", 2 => "out-of-stock", 3 => "low", _ => "" };
            bool? hasImage = ImageFilter.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            // Extensions can replace catalog quantities with a location-specific balance. Fetch
            // the full stock set first, then evaluate availability against the displayed quantity.
            var items = await _viewModel.GetInventoryAsync(FilterQueryBox.Text, status, currency, "", hasImage);
            foreach (var module in _modules)
            foreach (var extension in module.Registry.InventoryExtensions)
            {
                if (!module.Context.GrantedPermissions.Contains(extension.RequiredPermission)) continue;
                items = await extension.ApplyFilterAsync(module.Context, items);
            }
            items = InventoryStockFilter.Apply(items, stock);
            var id = selectedProductId ?? (InventoryGrid.SelectedItem as InventoryItemInfo)?.Id;
            InventoryGrid.ItemsSource = items;
            var activeCount = items.Count(x => x.Active);
            var lowStockCount = items.Count(x => x.MinimumStock > 0m && x.Quantity <= x.MinimumStock);
            InventorySummary.Text = $"{items.Count} productos · {activeCount} activos · {items.Count - activeCount} inactivos · {lowStockCount} bajo mínimo.";
            InventoryGrid.SelectedItem = id.HasValue
                ? items.FirstOrDefault(x => x.Id == id.Value)
                : items.FirstOrDefault(x => x.Active) ?? items.FirstOrDefault();
            if (InventoryGrid.SelectedItem is null) ResetImagePreview();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void ApplyFilters_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void FilterQuery_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await LoadAsync(); }
    private async void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        FilterQueryBox.Clear(); StatusFilter.SelectedIndex = 0; CurrencyFilter.SelectedIndex = 0; StockFilter.SelectedIndex = 0; ImageFilter.SelectedIndex = 0;
        await LoadAsync();
    }
    private async void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductCreateWindow(_viewModel) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.CreatedProductId);
            StatusLabel.Text = dialog.CompletionMessage;
        }
    }

    private async void InventoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => await LoadSelectedImageAsync();

    private async Task LoadSelectedImageAsync()
    {
        var version = ++_imageLoadVersion;
        var item = InventoryGrid.SelectedItem as InventoryItemInfo;
        ProductImagePreview.Source = null;
        ProductImagePreview.Visibility = Visibility.Collapsed;
        ProductImagePlaceholder.Visibility = Visibility.Visible;
        if (item is null)
        {
            ProductImagePlaceholder.Text = "Selecciona un producto para consultar su foto.";
            return;
        }
        if (!item.HasImage)
        {
            ProductImagePlaceholder.Text = "Sin foto";
            return;
        }

        ProductImagePlaceholder.Text = "Cargando foto…";
        try
        {
            var bytes = await _viewModel.GetProductImageAsync(item.Id);
            if (version != _imageLoadVersion || InventoryGrid.SelectedItem is not InventoryItemInfo current || current.Id != item.Id) return;
            if (bytes is null)
            {
                ProductImagePlaceholder.Text = "Foto no disponible";
                return;
            }
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            ProductImagePreview.Source = image;
            ProductImagePreview.Visibility = Visibility.Visible;
            ProductImagePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (version != _imageLoadVersion) return;
            ProductImagePlaceholder.Text = $"No se pudo mostrar la foto. {PosUserMessage.ForException(ex)}";
        }
    }

    private void ResetImagePreview()
    {
        _imageLoadVersion++;
        ProductImagePreview.Source = null;
        ProductImagePreview.Visibility = Visibility.Collapsed;
        ProductImagePlaceholder.Visibility = Visibility.Visible;
        ProductImagePlaceholder.Text = "Selecciona un producto para consultar su foto.";
    }

    private async void InventoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (InventoryGrid.SelectedItem is InventoryItemInfo item)
            await OpenProductDetailsAsync(item);
    }

    private async Task OpenProductDetailsAsync(InventoryItemInfo item)
    {
        var changed = new ProductDetailsWindow(_viewModel, item, _modules) { Owner = this }.ShowDialog() == true;
        await LoadAsync(item.Id);
        if (changed)
            StatusLabel.Text = "Ficha del producto actualizada.";
    }
}
