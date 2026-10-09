using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductPriceWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly InventoryItemInfo _product;
    public ProductPriceWindow(PosViewModel viewModel, InventoryItemInfo product)
    {
        InitializeComponent(); _viewModel = viewModel; _product = product;
        ProductLabel.Text = $"{product.Sku} · {product.Name} · actual {product.Currency} {product.UnitPrice:N2}";
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(PriceBox.Text, out var price) || price < 0m || decimal.Round(price, 4) != price)
        { ErrorLabel.Text = "El precio debe ser no negativo y tener hasta cuatro decimales."; return; }
        try { await _viewModel.ChangePriceAsync(_product.Id, price, ReasonBox.Text.Trim()); DialogResult = true; }
        catch (Exception ex) { ErrorLabel.Text = PosUserMessage.ForException(ex); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
