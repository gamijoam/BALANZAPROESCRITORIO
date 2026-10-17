using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductDetailsWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly Guid _productId;
    private readonly IReadOnlyList<LoadedDesktopModule> _modules;
    private ProductDetailsInfo? _details;
    private IReadOnlyList<ProductUnitInfo> _units = [];
    private bool _loading;
    private bool _hasChanges;
    private Guid? _currentPresentationUnitId;

    public ProductDetailsWindow(PosViewModel viewModel, InventoryItemInfo product, IReadOnlyList<LoadedDesktopModule>? modules = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _productId = product.Id;
        _modules = modules ?? [];
        var workArea = SystemParameters.WorkArea;
        Width = workArea.Width - 16;
        Height = workArea.Height - 16;
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
            InitializeCostButton.IsEnabled = _details.Active && _details.Quantity > 0m && !_details.UnitCost.HasValue;
            StockAdjustmentAmountLabel.Text = $"Cantidad {_details.BaseUnitAbbreviation} (+/−)";
            ApplyStockAdjustmentButton.IsEnabled = _details.Active;
            Price1Box.Text = _details.UnitPrice.ToString("N2", PosNumberFormat.Culture);
            Price2Box.Text = _details.Price2?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
            Price3Box.Text = _details.Price3?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
            TaxTreatmentBox.SelectedItem = TaxTreatmentBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), _details.TaxTreatment, StringComparison.Ordinal));
            TitleProductLabel.Text = $"{_details.Name} · SKU {_details.Sku} · {_details.Currency}";
            StockSummaryLabel.Text = StockSummary(_details);
            BaseUnitSummaryLabel.Text = $"El inventario se cuenta por: {_details.BaseUnitName}";
            RemoveImageButton.IsEnabled = _details.HasImage;
            ImageStatusLabel.Text = _details.HasImage ? "Foto guardada" : "Sin foto";
            PresentationsGrid.ItemsSource = _details.Presentations
                .Select(x => x with { BaseUnitName = _details.BaseUnitName }).ToArray();
            NewPresentationFields();
            StatusLabel.Text = string.Empty;
            ProductImagePreview.Source = null;
            ProductImagePlaceholder.Visibility = _details.HasImage ? Visibility.Collapsed : Visibility.Visible;
            ProductImagePlaceholder.Text = "Sin foto";
            if (_details.HasImage)
            {
                try
                {
                    var imageBytes = await _viewModel.GetProductImageAsync(_productId);
                    if (imageBytes is { Length: > 0 })
                    {
                        ProductImagePreview.Source = LoadImage(imageBytes);
                        ProductImagePlaceholder.Visibility = Visibility.Collapsed;
                    }
                    else { ImageStatusLabel.Text = "No se pudo cargar la foto."; ProductImagePlaceholder.Text = "Foto no disponible"; ProductImagePlaceholder.Visibility = Visibility.Visible; }
                }
                catch (Exception) { ImageStatusLabel.Text = "No se pudo cargar la foto."; ProductImagePlaceholder.Text = "Foto no disponible"; ProductImagePlaceholder.Visibility = Visibility.Visible; }
            }
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
        finally { _loading = false; }
    }

    private static string StockSummary(ProductDetailsInfo details)
    {
        var parts = new List<string> { QuantityAndUnit(details.Quantity, details.BaseUnitName) };
        foreach (var presentation in details.Presentations.Where(x => x.Active && x.ConversionFactor > 0m))
        {
            var amount = details.Quantity / presentation.ConversionFactor;
            if (!presentation.AllowsFractionalQuantities) amount = decimal.Floor(amount);
            if (amount > 0m) parts.Add(QuantityAndUnit(amount, presentation.UnitName));
        }
        return string.Join("  ·  ", parts);
    }

    private static string QuantityAndUnit(decimal quantity, string unitName)
    {
        var name = quantity == 1m ? unitName : PluralizeUnit(unitName);
        return $"{PosNumberFormat.FormatQuantity(quantity)} {name}";
    }

    private static string PluralizeUnit(string unitName)
    {
        var name = unitName.Trim();
        if (name.EndsWith('z')) return name[..^1] + "ces";
        if (name.EndsWith('s')) return name;
        if (name.EndsWith("ión", StringComparison.OrdinalIgnoreCase)) return name[..^3] + "iones";
        if (name.EndsWith("ón", StringComparison.OrdinalIgnoreCase)) return name[..^2] + "ones";
        return name.EndsWith('r') || name.EndsWith('d') || name.EndsWith('n') || name.EndsWith('j')
            ? name + "es"
            : name + "s";
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
            _hasChanges = true;
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
            _hasChanges = true;
            await LoadAsync();
            DialogResult = true;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        var historyExtension = _modules
            .SelectMany(module => module.Registry.InventoryHistoryExtensions
                .Where(extension => module.Context.GrantedPermissions.Contains(extension.RequiredPermission))
                .Select(extension => (module.Context, Extension: extension)))
            .FirstOrDefault();
        new InventoryMovementsWindow(_viewModel, ToInventoryItem(), historyExtension.Extension is null ? null : historyExtension.Context,
            historyExtension.Extension) { Owner = this }.ShowDialog();
    }

    private InventoryItemInfo ToInventoryItem() => new(_details!.Id, _details.Sku, _details.Name, _details.UnitPrice,
        _details.Currency, _details.Quantity, _details.HasImage)
    {
        Active = _details.Active, Price2 = _details.Price2, Price3 = _details.Price3, UnitCost = _details.UnitCost,
        MinimumStock = _details.MinimumStock, TaxTreatment = _details.TaxTreatment,
        BaseUnitId = _details.BaseUnitId, BaseUnitName = _details.BaseUnitName,
        BaseUnitAbbreviation = _details.BaseUnitAbbreviation,
        BaseUnitAllowsFractional = _details.BaseUnitAllowsFractionalQuantities
    };

    private async void InitializeCost_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null || !_details.Active || _details.Quantity <= 0m || _details.UnitCost.HasValue) return;
        if (new ProductPriceWindow(_viewModel, ToInventoryItem(), initialCostMode: true) { Owner = this }.ShowDialog() != true) return;
        _hasChanges = true;
        await LoadAsync();
        StatusLabel.Text = "Costo inicial guardado y registrado.";
    }

    private async void ApplyStockAdjustment_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null || !_details.Active) return;
        if (!PosNumberFormat.TryParseAmount(StockAdjustmentAmountBox.Text, out var delta) || delta == 0m || decimal.Round(delta, 3) != delta ||
            !_details.BaseUnitAllowsFractionalQuantities && decimal.Round(delta, 0) != delta)
        {
            StatusLabel.Text = _details.BaseUnitAllowsFractionalQuantities
                ? "El ajuste debe ser distinto de cero y tener hasta tres decimales."
                : "Esta unidad solo permite ajustes enteros.";
            return;
        }

        ApplyStockAdjustmentButton.IsEnabled = false;
        try
        {
            var quantity = await _viewModel.AdjustStockAsync(_productId, delta, StockAdjustmentReasonBox.Text.Trim());
            _hasChanges = true;
            StockAdjustmentAmountBox.Text = "1";
            StockAdjustmentReasonBox.Clear();
            await LoadAsync();
            StatusLabel.Text = $"Existencia actualizada: {PosNumberFormat.FormatQuantity(quantity)} {_details.BaseUnitAbbreviation}.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
        finally { ApplyStockAdjustmentButton.IsEnabled = _details?.Active == true; }
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
            ProductImagePlaceholder.Visibility = Visibility.Collapsed;
            RemoveImageButton.IsEnabled = true;
            ImageStatusLabel.Text = "Foto guardada.";
            _hasChanges = true;
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
            ProductImagePlaceholder.Text = "Sin foto";
            ProductImagePlaceholder.Visibility = Visibility.Visible;
            RemoveImageButton.IsEnabled = false;
            ImageStatusLabel.Text = "Foto quitada.";
            _hasChanges = true;
        }
        catch (Exception ex) { ImageStatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private void PresentationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresentationsGrid.SelectedItem is not ProductPresentationInfo presentation) return;
        PresentationUnitBox.SelectedItem = _units.FirstOrDefault(x => x.Id == presentation.UnitId);
        ConversionFactorBox.Text = presentation.ConversionFactor.ToString("0.###", PosNumberFormat.Culture);
        PresentationPrice1Box.Text = presentation.Price1?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
        PresentationPrice2Box.Text = presentation.Price2?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
        PresentationPrice3Box.Text = presentation.Price3?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
        PresentationBarcodeBox.Text = presentation.Barcode ?? string.Empty;
        PresentationAdvancedOptions.IsExpanded = presentation.Price1.HasValue || presentation.Price2.HasValue ||
            presentation.Price3.HasValue || !string.IsNullOrWhiteSpace(presentation.Barcode);
        _editingPresentationId = presentation.Id;
        _currentPresentationUnitId = presentation.UnitId;
        PresentationEditorTitle.Text = $"Editar forma: {presentation.UnitName}";
        SavePresentationButton.Content = presentation.Active ? "Guardar cambios" : "Reactivar";
        DeactivatePresentationButton.IsEnabled = presentation.Active;
        CancelPresentationEditButton.Visibility = Visibility.Visible;
        UpdateConversionHint();
    }

    private Guid? _editingPresentationId;

    private void NewPresentation_Click(object sender, RoutedEventArgs e) => NewPresentationFields();

    private void NewPresentationFields()
    {
        _editingPresentationId = null;
        _currentPresentationUnitId = null;
        PresentationEditorTitle.Text = "Agregar forma de venta";
        PresentationUnitBox.SelectedItem = null;
        ConversionFactorPanel.Visibility = Visibility.Collapsed;
        ConversionFactorBox.Clear();
        PresentationPrice1Box.Clear(); PresentationPrice2Box.Clear(); PresentationPrice3Box.Clear(); PresentationBarcodeBox.Clear();
        PresentationAdvancedOptions.IsExpanded = false;
        SavePresentationButton.Content = "Agregar";
        DeactivatePresentationButton.IsEnabled = false;
        CancelPresentationEditButton.Visibility = Visibility.Collapsed;
        PresentationsGrid.SelectedItem = null;
    }

    private void CancelPresentationEdit_Click(object sender, RoutedEventArgs e) => NewPresentationFields();

    private void PresentationUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _details is null || PresentationUnitBox.SelectedItem is not ProductUnitInfo unit) return;
        ConversionFactorPanel.Visibility = Visibility.Visible;
        ConversionFactorLabel.Text = $"Contenido por cada {unit.Name}";
        BaseUnitFactorLabel.Text = PluralizeUnit(_details.BaseUnitName);
        if (!_editingPresentationId.HasValue || _currentPresentationUnitId != unit.Id)
            ConversionFactorBox.Text = DefaultConversionFactor(unit)?.ToString("0.###", PosNumberFormat.Culture) ?? string.Empty;
        _currentPresentationUnitId = unit.Id;
        UpdateConversionHint();
    }

    private decimal? DefaultConversionFactor(ProductUnitInfo unit)
    {
        if (_details is null || unit.Id == _details.BaseUnitId) return 1m;
        var unitName = NormalizeUnitName(unit.Name);
        var baseName = NormalizeUnitName(_details.BaseUnitName);
        if (unitName is "par" or "pareja") return 2m;
        if (unitName is "docena") return 12m;
        if ((unitName is "gramo" or "gramos") && (baseName is "kilogramo" or "kilogramos")) return 0.001m;
        if ((unitName is "kilogramo" or "kilogramos") && (baseName is "gramo" or "gramos")) return 1000m;
        if (unitName is "caja" or "cajas" or "paquete" or "paquetes" or "pack" or "packs" or "bulto" or "bultos" or
            "fardo" or "fardos" or "bolsa" or "bolsas" or "saco" or "sacos" or "paca" or "pacas" or "carton" or "cartones" or "lote" or "lotes")
            return null;
        return 1m;
    }

    private static string NormalizeUnitName(string name)
    {
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        return new string(decomposed
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            .ToArray()).Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private void ConversionFactorBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateConversionHint();

    private void UpdateConversionHint()
    {
        if (_details is null || PresentationUnitBox.SelectedItem is not ProductUnitInfo unit) return;
        if (PosNumberFormat.TryParseAmount(ConversionFactorBox.Text, out var factor) && factor > 0m)
            ConversionHintLabel.Text = $"1 {unit.Name} = {PosNumberFormat.FormatQuantity(factor)} {PluralizeUnit(_details.BaseUnitName)}";
        else
            ConversionHintLabel.Text = $"Indica cuántas {PluralizeUnit(_details.BaseUnitName)} contiene cada {unit.Name}.";
    }

    private async void CreateUnit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductUnitCreateWindow(_units) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var unit = await _viewModel.CreateProductUnitAsync(dialog.UnitName, dialog.Abbreviation, dialog.AllowsFractional);
            _units = await _viewModel.GetProductUnitsAsync();
            NewPresentationFields();
            PresentationUnitBox.ItemsSource = _units; PresentationUnitBox.SelectedItem = _units.FirstOrDefault(x => x.Id == unit.Id);
            StatusLabel.Text = $"Unidad {unit.Name} agregada.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void SavePresentation_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        if (PresentationUnitBox.SelectedItem is not ProductUnitInfo unit)
        { StatusLabel.Text = "Elige cómo se vende este producto."; return; }
        if (!_editingPresentationId.HasValue && unit.Id == _details.BaseUnitId)
        { StatusLabel.Text = "La unidad del inventario ya está disponible para vender."; return; }
        if (!PosNumberFormat.TryParseAmount(ConversionFactorBox.Text, out var factor) || factor <= 0m || decimal.Round(factor, 3) != factor)
        { StatusLabel.Text = $"Indica cuántas {PluralizeUnit(_details.BaseUnitName)} contiene cada {unit.Name}."; return; }
        if (!_details.BaseUnitAllowsFractionalQuantities && decimal.Round(factor, 0) != factor)
        { StatusLabel.Text = "La unidad del inventario solo permite cantidades enteras."; return; }
        if (!TryOptionalPrice(PresentationPrice1Box.Text, out var price1) || !TryOptionalPrice(PresentationPrice2Box.Text, out var price2) ||
            !TryOptionalPrice(PresentationPrice3Box.Text, out var price3))
        { StatusLabel.Text = "Revisa los precios especiales. Déjalos vacíos para usar el cálculo automático."; return; }
        var wasEditing = _editingPresentationId.HasValue;
        try
        {
            await _viewModel.SaveProductPresentationAsync(_productId, new ProductPresentationInput(_editingPresentationId, unit.Id,
                unit.Name, factor, price1, price2, price3, PresentationBarcodeBox.Text.Trim()));
            _hasChanges = true;
            _editingPresentationId = null;
            await LoadAsync();
            StatusLabel.Text = wasEditing ? "Cambios guardados." : "Forma de venta agregada.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void DeactivatePresentation_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPresentationId is not { } id || PresentationsGrid.SelectedItem is not ProductPresentationInfo presentation) return;
        if (MessageBox.Show(this, $"¿Retirar «{presentation.UnitName}» de las formas de venta? Las ventas anteriores se conservarán.",
            "Retirar forma de venta", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.DeactivateProductPresentationAsync(_productId, id);
            _hasChanges = true;
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

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = _hasChanges;
}
