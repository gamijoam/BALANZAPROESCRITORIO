using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Win32;
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
            var stock = StockFilter.SelectedIndex switch { 1 => "available", 2 => "out-of-stock", _ => "" };
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
            InventorySummary.Text = $"{items.Count} productos · {activeCount} activos · {items.Count - activeCount} inactivos.";
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
        SelectedProductName.Text = item?.Name ?? "Selecciona un producto";
        SelectedProductSku.Text = item is null ? string.Empty : $"Código: {item.Sku}";
        ProductImagePreview.Source = null;
        ProductImagePreview.Visibility = Visibility.Collapsed;
        ProductImagePlaceholder.Visibility = Visibility.Visible;
        UploadImageButton.IsEnabled = item is not null;
        RemoveImageButton.IsEnabled = item?.HasImage == true;
        EditProductButton.IsEnabled = item is not null;
        ToggleProductActiveButton.IsEnabled = item is not null;
        ToggleProductActiveButton.Content = item?.Active == true ? "Desactivar producto" : "Reactivar producto";
        ChangePriceButton.IsEnabled = item?.Active == true;
        ChangeTaxTreatmentButton.IsEnabled = item is not null;
        TaxTreatmentStatusLabel.Text = item is null ? string.Empty : $"Impuesto: {item.TaxTreatmentLabel}";
        ChangeTaxTreatmentButton.Content = item?.TaxTreatment == "Unclassified" ? "Asignar impuesto" : "Cambiar impuesto";
        AdjustButton.IsEnabled = item?.Active == true;
        if (item is null)
        {
            ImageStatusLabel.Text = string.Empty;
            ProductImagePlaceholder.Text = "Selecciona un producto para consultar su foto.";
            return;
        }
        if (!item.HasImage)
        {
            ImageStatusLabel.Text = "Este producto no tiene foto guardada.";
            ProductImagePlaceholder.Text = "Sin foto";
            return;
        }

        ImageStatusLabel.Text = "Cargando foto…";
        ProductImagePlaceholder.Text = "Cargando foto…";
        try
        {
            var bytes = await _viewModel.GetProductImageAsync(item.Id);
            if (version != _imageLoadVersion || InventoryGrid.SelectedItem is not InventoryItemInfo current || current.Id != item.Id) return;
            if (bytes is null)
            {
                ImageStatusLabel.Text = "No se encontró el archivo de la foto. Puedes subirla de nuevo o quitar la referencia.";
                ProductImagePlaceholder.Text = "Foto no disponible";
                return;
            }
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            ProductImagePreview.Source = image;
            ProductImagePreview.Visibility = Visibility.Visible;
            ProductImagePlaceholder.Visibility = Visibility.Collapsed;
            ImageStatusLabel.Text = "Foto guardada en el catálogo.";
        }
        catch (Exception ex)
        {
            if (version != _imageLoadVersion) return;
            ImageStatusLabel.Text = $"No se pudo cargar la foto: {PosUserMessage.ForException(ex)}";
            ProductImagePlaceholder.Text = "No se pudo mostrar la foto";
        }
    }

    private async void ProductImage_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        var picker = new OpenFileDialog { Title = $"Foto de {item.Name}", Filter = "Imágenes JPG, PNG o WebP|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var file = new FileInfo(picker.FileName);
        if (file.Length is <= 0 or > 5 * 1024 * 1024)
        {
            ImageStatusLabel.Text = "La foto debe pesar más de 0 y como máximo 5 MB.";
            return;
        }
        UploadImageButton.IsEnabled = false;
        RemoveImageButton.IsEnabled = false;
        ImageStatusLabel.Text = "Guardando foto…";
        try
        {
            await _viewModel.UploadProductImageAsync(item.Id, picker.FileName);
            await LoadAsync(item.Id);
            StatusLabel.Text = "Foto guardada en el producto.";
        }
        catch (Exception ex)
        {
            ImageStatusLabel.Text = $"No se pudo guardar la foto. Revisa el formato y vuelve a intentarlo. {PosUserMessage.ForException(ex)}";
            UploadImageButton.IsEnabled = true;
            RemoveImageButton.IsEnabled = item.HasImage;
        }
    }

    private async void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (!item.HasImage) { ImageStatusLabel.Text = "Este producto no tiene foto para quitar."; return; }
        if (MessageBox.Show(this, $"¿Quitar la foto de «{item.Name}»?", "Quitar foto", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UploadImageButton.IsEnabled = false;
        RemoveImageButton.IsEnabled = false;
        ImageStatusLabel.Text = "Quitando foto…";
        try
        {
            await _viewModel.RemoveProductImageAsync(item.Id);
            await LoadAsync(item.Id);
            StatusLabel.Text = "Foto quitada del producto.";
        }
        catch (Exception ex)
        {
            ImageStatusLabel.Text = $"No se pudo quitar la foto: {PosUserMessage.ForException(ex)}";
            UploadImageButton.IsEnabled = true;
            RemoveImageButton.IsEnabled = item.HasImage;
        }
    }

    private void ResetImagePreview()
    {
        _imageLoadVersion++;
        SelectedProductName.Text = "Selecciona un producto";
        SelectedProductSku.Text = string.Empty;
        ImageStatusLabel.Text = string.Empty;
        ProductImagePreview.Source = null;
        ProductImagePreview.Visibility = Visibility.Collapsed;
        ProductImagePlaceholder.Visibility = Visibility.Visible;
        ProductImagePlaceholder.Text = "Selecciona un producto para consultar su foto.";
        UploadImageButton.IsEnabled = false;
        RemoveImageButton.IsEnabled = false;
        EditProductButton.IsEnabled = false;
        ToggleProductActiveButton.IsEnabled = false;
        ChangePriceButton.IsEnabled = false;
        ChangeTaxTreatmentButton.IsEnabled = false;
        TaxTreatmentStatusLabel.Text = string.Empty;
        AdjustButton.IsEnabled = false;
    }

    private async void ChangePrice_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (!item.Active) { StatusLabel.Text = "Reactiva el producto antes de cambiar su precio."; return; }
        if (new ProductPriceWindow(_viewModel, item) { Owner = this }.ShowDialog() == true)
        { StatusLabel.Text = "Precio actualizado."; await LoadAsync(); }
    }

    private async void ChangeTaxTreatment_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (new ProductTaxTreatmentWindow(_viewModel, item) { Owner = this }.ShowDialog() == true)
        {
            await LoadAsync(item.Id);
            StatusLabel.Text = "Regla de impuesto guardada en el producto.";
        }
    }

    private async void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (new ProductEditWindow(_viewModel, item) { Owner = this }.ShowDialog() == true)
        {
            await LoadAsync(item.Id);
            StatusLabel.Text = "Datos del producto actualizados y registrados en auditoría.";
        }
    }

    private async void ToggleProductActive_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        var active = !item.Active;
        var verb = active ? "reactivar" : "desactivar";
        var title = active ? "Reactivar producto" : "Desactivar producto";
        if (MessageBox.Show(this, $"¿Deseas {verb} «{item.Name}»?", title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.SetProductActiveAsync(item.Id, active);
            if (StatusFilter.SelectedIndex != 2) StatusFilter.SelectedIndex = 2;
            await LoadAsync(item.Id);
            StatusLabel.Text = string.Empty;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (!item.Active) { StatusLabel.Text = "Reactiva el producto antes de ajustar su existencia."; return; }
        if (!PosNumberFormat.TryParseAmount(DeltaBox.Text, out var delta) || delta == 0m || decimal.Round(delta, 3) != delta)
        { StatusLabel.Text = "El ajuste debe ser distinto de cero y tener hasta tres decimales."; return; }
        try
        {
            var quantity = await _viewModel.AdjustStockAsync(item.Id, delta, ReasonBox.Text.Trim());
            StatusLabel.Text = $"Existencia actualizada: {PosNumberFormat.FormatQuantity(quantity)}.";
            ReasonBox.Clear(); await LoadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        var historyExtension = _modules
            .SelectMany(module => module.Registry.InventoryHistoryExtensions
                .Where(extension => module.Context.GrantedPermissions.Contains(extension.RequiredPermission))
                .Select(extension => (module.Context, Extension: extension)))
            .FirstOrDefault();
        new InventoryMovementsWindow(_viewModel, item, historyExtension.Extension is null ? null : historyExtension.Context,
            historyExtension.Extension) { Owner = this }.ShowDialog();
    }
}
