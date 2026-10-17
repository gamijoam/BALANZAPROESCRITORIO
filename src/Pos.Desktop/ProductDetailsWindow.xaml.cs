using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductDetailsWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly Guid _productId;
    private ProductDetailsInfo? _details;
    private IReadOnlyList<ProductUnitInfo> _units = [];
    private bool _loading;

    public ProductDetailsWindow(PosViewModel viewModel, InventoryItemInfo product)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _productId = product.Id;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width - 32);
        Height = Math.Min(Height, workArea.Height - 48);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var detailsTask = _viewModel.GetProductDetailsAsync(_productId);
            var unitsTask = _viewModel.GetProductUnitsAsync();
            await Task.WhenAll(detailsTask, unitsTask);
            _details = detailsTask.Result;
            _units = unitsTask.Result;
            BaseUnitBox.ItemsSource = _units;
            PresentationUnitBox.ItemsSource = _units;
            SkuBox.Text = _details.Sku;
            NameBox.Text = _details.Name;
            BaseUnitBox.SelectedItem = _units.FirstOrDefault(x => x.Id == _details.BaseUnitId);
            MinimumStockBox.Text = _details.MinimumStock.ToString("0.###", PosNumberFormat.Culture);
            MinimumStockLabel.Text = $"Aviso de bajo stock ({_details.BaseUnitAbbreviation})";
            ActiveLabel.Text = _details.Active ? "Activo" : "Inactivo";
            ToggleActiveButton.Content = _details.Active ? "Desactivar producto" : "Reactivar producto";
            UnitCostLabel.Text = _details.UnitCost.HasValue
                ? $"{_details.Currency} {_details.UnitCost.Value:N4} por {_details.BaseUnitAbbreviation}"
                : "Sin costo registrado";
            Price1Box.Text = _details.UnitPrice.ToString("N2", PosNumberFormat.Culture);
            Price2Box.Text = _details.Price2?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
            Price3Box.Text = _details.Price3?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
            TaxTreatmentBox.SelectedItem = TaxTreatmentBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), _details.TaxTreatment, StringComparison.Ordinal));
            TitleProductLabel.Text = $"{_details.Name} · SKU {_details.Sku} · {_details.Currency}";
            StockSummaryLabel.Text = StockSummary(_details);
            RemoveImageButton.IsEnabled = _details.HasImage;
            ImageStatusLabel.Text = _details.HasImage ? "Foto guardada" : "Sin foto";
            PresentationsGrid.ItemsSource = _details.Presentations;
            NewPresentationFields();
            StatusLabel.Text = string.Empty;
            ProductImagePreview.Source = null;
            if (_details.HasImage)
            {
                try
                {
                    var imageBytes = await _viewModel.GetProductImageAsync(_productId);
                    if (imageBytes is { Length: > 0 }) ProductImagePreview.Source = LoadImage(imageBytes);
                    else ImageStatusLabel.Text = "No se pudo cargar la foto.";
                }
                catch (Exception) { ImageStatusLabel.Text = "No se pudo cargar la foto."; }
            }
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
        finally { _loading = false; }
    }

    private static string StockSummary(ProductDetailsInfo details)
    {
        var parts = new List<string> { $"{PosNumberFormat.FormatQuantity(details.Quantity)} {details.BaseUnitAbbreviation}" };
        foreach (var presentation in details.Presentations.Where(x => x.Active && x.ConversionFactor > 0m))
        {
            var amount = details.Quantity / presentation.ConversionFactor;
            if (!presentation.AllowsFractionalQuantities) amount = decimal.Floor(amount);
            if (amount > 0m) parts.Add($"{PosNumberFormat.FormatQuantity(amount)} {presentation.Label}");
        }
        return string.Join("  ·  ", parts);
    }

    private async void SaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null || BaseUnitBox.SelectedItem is not ProductUnitInfo baseUnit ||
            TaxTreatmentBox.SelectedItem is not ComboBoxItem taxItem || taxItem.Tag is not string taxTreatment)
        { StatusLabel.Text = "Completa la unidad base y el tratamiento de impuesto."; return; }

        if (!PosNumberFormat.TryParseAmount(MinimumStockBox.Text, out var minimumStock) || minimumStock < 0m || decimal.Round(minimumStock, 3) != minimumStock ||
            !baseUnit.AllowsFractionalQuantities && decimal.Round(minimumStock, 0) != minimumStock)
        { StatusLabel.Text = baseUnit.AllowsFractionalQuantities ? "El mínimo debe ser cero o una cantidad válida de hasta tres decimales." : "El mínimo debe ser un número entero para esta unidad."; return; }
        if (!PosNumberFormat.TryParseAmount(Price1Box.Text, out var price1) || price1 <= 0m || decimal.Round(price1, 4) != price1 ||
            !TryOptionalPrice(Price2Box.Text, out var price2) || !TryOptionalPrice(Price3Box.Text, out var price3))
        { StatusLabel.Text = "Revisa los precios. El Precio 1 y los precios configurados deben ser mayores que cero."; return; }
        if (_details.Price2.HasValue && !price2.HasValue || _details.Price3.HasValue && !price3.HasValue)
        { StatusLabel.Text = "Para mantener el historial, no se puede borrar Precio 2 o Precio 3 desde esta ficha."; return; }

        var priceChanged = price1 != _details.UnitPrice || price2 != _details.Price2 || price3 != _details.Price3;
        var priceReason = PriceReasonBox.Text.Trim();
        if (priceChanged && priceReason.Length is < 3 or > 250)
        { StatusLabel.Text = "Indica un motivo de 3 a 250 caracteres para guardar los cambios de precio."; PriceReasonBox.Focus(); return; }

        SaveDetailsButton.IsEnabled = false;
        try
        {
            await _viewModel.SaveProductDetailsAsync(_productId, SkuBox.Text.Trim(), NameBox.Text.Trim(), minimumStock, baseUnit.Id);
            if (taxTreatment != _details.TaxTreatment)
                await _viewModel.SetProductTaxTreatmentAsync(_productId, taxTreatment);
            if (price1 != _details.UnitPrice) await _viewModel.ChangePriceAsync(_productId, "Price1", price1, priceReason);
            if (price2.HasValue && price2 != _details.Price2) await _viewModel.ChangePriceAsync(_productId, "Price2", price2.Value, priceReason);
            if (price3.HasValue && price3 != _details.Price3) await _viewModel.ChangePriceAsync(_productId, "Price3", price3.Value, priceReason);
            DialogResult = true;
        }
        catch (Exception ex) { StatusLabel.Text = $"No se guardaron todos los cambios: {PosUserMessage.ForException(ex)}"; }
        finally { SaveDetailsButton.IsEnabled = true; }
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        var next = !_details.Active;
        var action = next ? "reactivar" : "desactivar";
        if (MessageBox.Show(this, $"¿Deseas {action} «{_details.Name}»?", "Estado del producto", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.SetProductActiveAsync(_productId, next);
            await LoadAsync();
            DialogResult = true;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        var item = new InventoryItemInfo(_details.Id, _details.Sku, _details.Name, _details.UnitPrice,
            _details.Currency, _details.Quantity, _details.HasImage)
        {
            Active = _details.Active, Price2 = _details.Price2, Price3 = _details.Price3, UnitCost = _details.UnitCost,
            MinimumStock = _details.MinimumStock, TaxTreatment = _details.TaxTreatment,
            BaseUnitId = _details.BaseUnitId, BaseUnitName = _details.BaseUnitName,
            BaseUnitAbbreviation = _details.BaseUnitAbbreviation,
            BaseUnitAllowsFractional = _details.BaseUnitAllowsFractionalQuantities
        };
        new InventoryMovementsWindow(_viewModel, item) { Owner = this }.ShowDialog();
    }

    private async void UploadImage_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        var picker = new OpenFileDialog { Title = $"Foto de {_details.Name}", Filter = "Imágenes JPG, PNG o WebP|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var file = new FileInfo(picker.FileName);
        if (file.Length is <= 0 or > 5 * 1024 * 1024) { ImageStatusLabel.Text = "La imagen debe pesar hasta 5 MB."; return; }
        try
        {
            await _viewModel.UploadProductImageAsync(_productId, picker.FileName);
            _details = _details with { HasImage = true };
            ProductImagePreview.Source = LoadImage(File.ReadAllBytes(picker.FileName));
            RemoveImageButton.IsEnabled = true;
            ImageStatusLabel.Text = "Foto guardada.";
        }
        catch (Exception ex) { ImageStatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_details?.HasImage != true) return;
        if (MessageBox.Show(this, "¿Quitar la foto de este producto?", "Foto del producto", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.RemoveProductImageAsync(_productId);
            _details = _details with { HasImage = false };
            ProductImagePreview.Source = null;
            RemoveImageButton.IsEnabled = false;
            ImageStatusLabel.Text = "Foto quitada.";
        }
        catch (Exception ex) { ImageStatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private void PresentationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresentationsGrid.SelectedItem is not ProductPresentationInfo presentation) return;
        PresentationLabelBox.Text = presentation.Label;
        PresentationUnitBox.SelectedItem = _units.FirstOrDefault(x => x.Id == presentation.UnitId);
        ConversionFactorBox.Text = presentation.ConversionFactor.ToString("0.###", PosNumberFormat.Culture);
        PresentationPrice1Box.Text = presentation.Price1?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
        PresentationPrice2Box.Text = presentation.Price2?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
        PresentationPrice3Box.Text = presentation.Price3?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
        PresentationBarcodeBox.Text = presentation.Barcode ?? string.Empty;
        _editingPresentationId = presentation.Id;
        SavePresentationButton.Content = presentation.Active ? "Guardar" : "Reactivar";
        DeactivatePresentationButton.IsEnabled = presentation.Active;
    }

    private Guid? _editingPresentationId;

    private void NewPresentation_Click(object sender, RoutedEventArgs e) => NewPresentationFields();

    private void NewPresentationFields()
    {
        _editingPresentationId = null;
        PresentationLabelBox.Clear();
        PresentationUnitBox.SelectedItem = _units.FirstOrDefault(x => x.Id == _details?.BaseUnitId) ?? _units.FirstOrDefault();
        ConversionFactorBox.Text = "1";
        PresentationPrice1Box.Clear(); PresentationPrice2Box.Clear(); PresentationPrice3Box.Clear(); PresentationBarcodeBox.Clear();
        SavePresentationButton.Content = "Guardar";
        DeactivatePresentationButton.IsEnabled = false;
        PresentationsGrid.SelectedItem = null;
    }

    private async void CreateUnit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductUnitCreateWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var unit = await _viewModel.CreateProductUnitAsync(dialog.UnitName, dialog.Abbreviation, dialog.AllowsFractional);
            _units = await _viewModel.GetProductUnitsAsync();
            PresentationUnitBox.ItemsSource = _units; PresentationUnitBox.SelectedItem = _units.FirstOrDefault(x => x.Id == unit.Id);
            StatusLabel.Text = $"Se creó la unidad {unit.DisplayLabel}. Puedes usarla en una presentación de venta.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void SavePresentation_Click(object sender, RoutedEventArgs e)
    {
        if (_details is not null && !_details.BaseUnitAllowsFractionalQuantities &&
            PosNumberFormat.TryParseAmount(ConversionFactorBox.Text, out var baseFactor) && decimal.Round(baseFactor, 0) != baseFactor)
        { StatusLabel.Text = "La unidad base no permite fracciones; usa un factor entero para esta presentación."; return; }
        if (_details is null || PresentationUnitBox.SelectedItem is not ProductUnitInfo unit ||
            !PosNumberFormat.TryParseAmount(ConversionFactorBox.Text, out var factor) || factor <= 0m || decimal.Round(factor, 3) != factor ||
            !TryOptionalPrice(PresentationPrice1Box.Text, out var price1) || !TryOptionalPrice(PresentationPrice2Box.Text, out var price2) ||
            !TryOptionalPrice(PresentationPrice3Box.Text, out var price3))
        { StatusLabel.Text = "Revisa la unidad, el factor y los precios de la presentación."; return; }
        var label = PresentationLabelBox.Text.Trim();
        if (label.Length is < 2 or > 80) { StatusLabel.Text = "Escribe un nombre visible de 2 a 80 caracteres."; return; }
        try
        {
            await _viewModel.SaveProductPresentationAsync(_productId, new ProductPresentationInput(_editingPresentationId, unit.Id,
                label, factor, price1, price2, price3, PresentationBarcodeBox.Text.Trim()));
            _editingPresentationId = null;
            await LoadAsync();
            StatusLabel.Text = "Presentación guardada.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void DeactivatePresentation_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPresentationId is not { } id || PresentationsGrid.SelectedItem is not ProductPresentationInfo presentation) return;
        if (MessageBox.Show(this, $"¿Desactivar «{presentation.Label}»? Las ventas anteriores conservarán su presentación.",
            "Desactivar presentación", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.DeactivateProductPresentationAsync(_productId, id);
            await LoadAsync();
            StatusLabel.Text = "Presentación desactivada.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private static bool TryOptionalPrice(string text, out decimal? price)
    {
        price = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!PosNumberFormat.TryParseAmount(text, out var value) || value <= 0m || decimal.Round(value, 4) != value) return false;
        price = value;
        return true;
    }

    private static BitmapImage LoadImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
