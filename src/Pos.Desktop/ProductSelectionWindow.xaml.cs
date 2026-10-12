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
        _imageLoader = imageLoader;
        _products = new ObservableCollection<ProductSelectionEntry>(products.Select(x => new ProductSelectionEntry(x)));
        ProductsList.ItemsSource = _products;
        TitleLabel.Text = products.Count == 1
            ? $"1 producto encontrado para «{searchText}»"
            : $"{products.Count} productos encontrados para «{searchText}»";
    }

    public ProductInfo? SelectedProduct => (ProductsList.SelectedItem as ProductSelectionEntry)?.Product;

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
        AddProductButton.IsEnabled = entry?.Product.StockQuantity is null or > 0m;
        SelectionStatusText.Text = entry?.Product.StockQuantity is <= 0m
            ? "Producto agotado: no se puede agregar a la venta."
            : string.Empty;
    }

    private void AcceptSelection()
    {
        if (SelectedProduct is null) return;
        if (SelectedProduct.StockQuantity is <= 0m)
        {
            SelectionStatusText.Text = "Producto agotado: no se puede agregar a la venta.";
            return;
        }
        DialogResult = true;
    }
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
        : quantity <= 0m ? "0 · AGOTADO" : PosNumberFormat.FormatQuantity(quantity);

    public void SetImage(ImageSource? image, string placeholderText)
    {
        ImageSource = image;
        ImagePlaceholderText = placeholderText;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
