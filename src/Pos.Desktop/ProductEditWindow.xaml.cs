using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductEditWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly InventoryItemInfo _product;

    public ProductEditWindow(PosViewModel viewModel, InventoryItemInfo product)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _product = product;
        SkuBox.Text = product.Sku;
        NameBox.Text = product.Name;
        MinimumStockBox.Text = PosNumberFormat.FormatQuantity(product.MinimumStock);
        Loaded += (_, _) => { SkuBox.Focus(); SkuBox.SelectAll(); };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var sku = SkuBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (!PosNumberFormat.TryParseAmount(MinimumStockBox.Text, out var minimumStock) || minimumStock < 0m || decimal.Round(minimumStock, 3) != minimumStock)
        {
            StatusLabel.Text = "El mínimo debe ser un número igual o mayor que cero, con hasta tres decimales.";
            return;
        }
        if (sku.Length is < 1 or > 64 || name.Length is < 2 or > 200)
        {
            StatusLabel.Text = "El SKU debe tener entre 1 y 64 caracteres y el nombre entre 2 y 200.";
            return;
        }
        try
        {
            await _viewModel.UpdateProductAsync(_product.Id, sku, name, minimumStock: minimumStock);
            DialogResult = true;
        }
        catch (PosApiException ex) when (ex.Code == "sku_already_exists")
        {
            StatusLabel.Text = "Ese SKU ya está asignado a otro producto.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = PosUserMessage.ForException(ex);
        }
    }
}
