using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductCreateWindow : Window
{
    private readonly PosViewModel _viewModel;
    private string? _imagePath;
    public Guid? CreatedProductId { get; private set; }
    public string CompletionMessage { get; private set; } = "Producto creado.";
    public ProductCreateWindow(PosViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = string.Empty;
        var taxTreatment = (TaxTreatmentBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(taxTreatment))
        { ErrorLabel.Text = "Selecciona cómo se calcula el impuesto de este producto."; TaxTreatmentBox.Focus(); return; }
        if (!PosNumberFormat.TryParseAmount(PriceBox.Text, out var price) || price < 0m || decimal.Round(price, 4) != price ||
            !TryOptionalPrice(Price2Box.Text, out var price2) || !TryOptionalPrice(Price3Box.Text, out var price3) ||
            !PosNumberFormat.TryParseAmount(StockBox.Text, out var stock) || stock < 0m || decimal.Round(stock, 3) != stock ||
            !PosNumberFormat.TryParseAmount(MinimumStockBox.Text, out var minimumStock) || minimumStock < 0m || decimal.Round(minimumStock, 3) != minimumStock)
        { ErrorLabel.Text = "Revisa los precios, la existencia inicial y el mínimo de unidades."; return; }
        var currency = (CurrencyBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "VES";
        try
        {
            var product = await _viewModel.CreateProductAsync(SkuBox.Text.Trim(), NameBox.Text.Trim(), price, price2, price3,
                taxTreatment, currency, stock, minimumStock: minimumStock);
            CreatedProductId = product.Id;
            if (_imagePath is not null)
            {
                try
                {
                    await _viewModel.UploadProductImageAsync(product.Id, _imagePath);
                    CompletionMessage = "Producto y foto guardados.";
                }
                catch (Exception imageError)
                {
                    CompletionMessage = $"Producto creado, pero no se guardó la foto. Puedes reintentarla desde Inventario. {PosUserMessage.ForException(imageError)}";
                    DialogResult = true;
                    return;
                }
            }
            else CompletionMessage = "Producto creado sin foto.";
            DialogResult = true;
        }
        catch (Exception ex) { ErrorLabel.Text = PosUserMessage.ForException(ex); }
    }
    private static bool TryOptionalPrice(string text, out decimal? price)
    {
        price = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!PosNumberFormat.TryParseAmount(text, out var parsed) || parsed < 0m || decimal.Round(parsed, 4) != parsed) return false;
        price = parsed;
        return true;
    }
    private void SelectImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Seleccionar foto del producto", Filter = "Imágenes JPG, PNG o WebP|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var file = new FileInfo(picker.FileName);
        if (file.Length is <= 0 or > 5 * 1024 * 1024) { ErrorLabel.Text = "La imagen debe pesar como máximo 5 MB."; return; }
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(picker.FileName); image.EndInit(); image.Freeze();
            _imagePath = picker.FileName;
            ImageNameLabel.Text = file.Name;
            ProductImagePreview.Source = image; ProductImagePreview.Visibility = Visibility.Visible; ErrorLabel.Text = string.Empty;
        }
        catch (Exception) { ErrorLabel.Text = "El archivo seleccionado no se puede mostrar como una imagen válida."; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
