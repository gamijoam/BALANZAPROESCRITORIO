using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductSelectionWindow : Window
{
    private readonly Func<Guid, Task<byte[]?>> _imageLoader;
    private readonly ObservableCollection<ProductSelectionEntry> _products;

    public ProductSelectionWindow(IReadOnlyList<ProductInfo> products, string searchText, Func<Guid, Task<byte[]?>> imageLoader)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1280, workArea.Width * 0.92);
        Height = Math.Min(800, workArea.Height * 0.9);
        MinWidth = Math.Min(900, Width);
        MinHeight = Math.Min(560, Height);
        _imageLoader = imageLoader;
        _products = new ObservableCollection<ProductSelectionEntry>(products.Select(x => new ProductSelectionEntry(x)));
        ProductsList.ItemsSource = _products;
        TitleLabel.Text = products.Count == 1
            ? $"1 producto encontrado para «{searchText}»"
            : $"{products.Count} productos encontrados para «{searchText}»";
    }

    public ProductInfo? SelectedProduct => (ProductsList.SelectedItem as ProductSelectionEntry)?.Product;
    public ProductSaleUnitInfo? SelectedSaleUnit => PresentationBox.SelectedItem as ProductSaleUnitInfo;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ProductsList.Items.Count > 0)
        {
            ProductsList.SelectedIndex = 0;
            ProductsList.Focus();
        }
        await LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        using var throttle = new SemaphoreSlim(4);
        var loads = _products.Where(x => x.Product.HasImage).Select(async entry =>
        {
            await throttle.WaitAsync();
            try
            {
                var bytes = await _imageLoader(entry.Product.Id);
                if (bytes is null || bytes.Length == 0)
                {
                    entry.SetImage(null, "Foto no disponible");
                    return;
                }

                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 160;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                entry.SetImage(bitmap, string.Empty);
            }
            catch (Exception)
            {
                entry.SetImage(null, "Foto no disponible");
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(loads);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AcceptSelection();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void Select_Click(object sender, RoutedEventArgs e) => AcceptSelection();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ProductsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var entry = ProductsList.SelectedItem as ProductSelectionEntry;
        IReadOnlyList<ProductSaleUnitInfo> units = entry is null ? Array.Empty<ProductSaleUnitInfo>() : AvailableUnits(entry.Product);
        PresentationBox.ItemsSource = units;
        PresentationBox.SelectedItem = entry is null ? null : units.FirstOrDefault(x => x.PresentationId == entry.Product.SuggestedPresentationId)
            ?? units.FirstOrDefault(x => x.PresentationId is null)
            ?? units.FirstOrDefault();
        UpdateSelectionAvailability();
    }

    private void PresentationBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateSelectionAvailability();

    private void UpdateSelectionAvailability()
    {
        var entry = ProductsList.SelectedItem as ProductSelectionEntry;
        var unit = SelectedSaleUnit;
        var requiredBaseUnits = unit?.ConversionFactor ?? 1m;
        var fractionalBaseStock = entry is not null && !entry.Product.BaseUnitAllowsFractional &&
            decimal.Round(requiredBaseUnits, 0) != requiredBaseUnits;
        AddProductButton.IsEnabled = entry?.Product.IsTaxClassified == true &&
            !fractionalBaseStock && (entry.Product.StockQuantity is null || entry.Product.StockQuantity >= requiredBaseUnits);
        SelectionStatusText.Text = entry?.Product.StockQuantity is <= 0m
            ? "Producto agotado: no se puede agregar a la venta."
            : entry is not null && !entry.Product.IsTaxClassified
                ? "Impuesto pendiente: clasifícalo en Inventario antes de venderlo."
                : fractionalBaseStock
                    ? "Esta presentación consumiría una fracción de la unidad base, que no está permitida."
                : entry?.Product.StockQuantity is { } stock && stock < requiredBaseUnits
                    ? $"No hay existencia suficiente para una unidad de {unit?.Label}."
                    : string.Empty;
    }

    private void AcceptSelection()
    {
        var product = SelectedProduct;
        var saleUnit = SelectedSaleUnit;
        if (product is null || saleUnit is null) return;
        if (product.StockQuantity is <= 0m)
        {
            SelectionStatusText.Text = "Producto agotado: no se puede agregar a la venta.";
            return;
        }
        if (!product.IsTaxClassified)
        {
            SelectionStatusText.Text = "Impuesto pendiente: clasifícalo en Inventario antes de venderlo.";
            return;
        }
        var fractionalBaseStock = !product.BaseUnitAllowsFractional &&
            decimal.Round(saleUnit.ConversionFactor, 0) != saleUnit.ConversionFactor;
        var insufficientStock = product.StockQuantity is { } available && available < saleUnit.ConversionFactor;
        if (fractionalBaseStock || insufficientStock)
        {
            SelectionStatusText.Text = fractionalBaseStock
                ? "Esta presentación consumiría una fracción de la unidad base, que no está permitida."
                : "La existencia no alcanza para la presentación seleccionada.";
            return;
        }
        DialogResult = true;
    }

    private static IReadOnlyList<ProductSaleUnitInfo> AvailableUnits(ProductInfo product)
        => product.SaleUnits.Count > 0 ? product.SaleUnits :
        [new ProductSaleUnitInfo(null, product.BaseUnitId, product.BaseUnitName, product.BaseUnitName,
            product.BaseUnitAbbreviation, 1m, product.UnitPrice, product.Price2, product.Price3, null, product.BaseUnitAllowsFractional)
            { BaseUnitAbbreviation = product.BaseUnitAbbreviation }];
}

public sealed class ProductSelectionEntry(ProductInfo product) : INotifyPropertyChanged
{
    private ImageSource? _imageSource;
    private string _imagePlaceholderText = product.HasImage ? "Cargando foto…" : "Sin foto";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ProductInfo Product { get; } = product;
    public ImageSource? ImageSource
    {
        get => _imageSource;
        private set { _imageSource = value; OnPropertyChanged(); }
    }
    public string ImagePlaceholderText
    {
        get => _imagePlaceholderText;
        private set { _imagePlaceholderText = value; OnPropertyChanged(); }
    }
    public string StockLabel => Product.StockQuantity is not { } quantity
        ? "No disponible"
        : quantity <= 0m ? "0 · AGOTADO" : QuantityWithUnit(quantity, Product.BaseUnitName);
    public string SaleUnitsSummary => Product.SaleUnits.Count == 0
        ? $"Se vende por: {Product.BaseUnitName}"
        : $"Se vende como: {string.Join(" · ", Product.SaleUnits.GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().DisplayLabel))}";

    private static string QuantityWithUnit(decimal quantity, string unitName)
    {
        if (quantity == 1m) return $"{PosNumberFormat.FormatQuantity(quantity)} {unitName}";
        var name = unitName.Trim();
        var plural = name.EndsWith('z') ? name[..^1] + "ces"
            : name.EndsWith('s') ? name
            : name.EndsWith("ión", StringComparison.OrdinalIgnoreCase) ? name[..^3] + "iones"
            : name.EndsWith("ón", StringComparison.OrdinalIgnoreCase) ? name[..^2] + "ones"
            : name.EndsWith('r') || name.EndsWith('d') || name.EndsWith('n') || name.EndsWith('j') ? name + "es"
            : name + "s";
        return $"{PosNumberFormat.FormatQuantity(quantity)} {plural}";
    }

    public void SetImage(ImageSource? image, string placeholderText)
    {
        ImageSource = image;
        ImagePlaceholderText = placeholderText;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
