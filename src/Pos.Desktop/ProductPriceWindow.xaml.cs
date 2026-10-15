using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductPriceWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly InventoryItemInfo _product;
    private readonly bool _initialCostMode;
    private bool _initializing = true;
    private string _priceTier = "Price1";
    public ProductPriceWindow(PosViewModel viewModel, InventoryItemInfo product, bool initialCostMode = false)
    {
        InitializeComponent(); _viewModel = viewModel; _product = product; _initialCostMode = initialCostMode;
        ProductLabel.Text = $"{product.Sku} · {product.Name} · Moneda {product.Currency}";
        if (initialCostMode)
        {
            Title = "Asignar costo inicial";
            HeadingLabel.Text = "ASIGNAR COSTO INICIAL";
            PriceTierHeader.Visibility = Visibility.Collapsed;
            PriceTierBox.Visibility = Visibility.Collapsed;
            CurrentPriceLabel.Text = $"Existencia actual: {product.Quantity:N3} · moneda {product.Currency} · sin costo promedio asignado.";
            AmountLabel.Text = "Costo unitario inicial";
            PriceBox.Clear();
            System.Windows.Automation.AutomationProperties.SetName(PriceBox, "Costo unitario inicial");
            ReasonLabel.Text = "Motivo o referencia del costo";
            System.Windows.Automation.AutomationProperties.SetName(ReasonBox, "Motivo o referencia del costo inicial");
            ModeHint.Text = "Indica el costo real por unidad que tenía la existencia actual. Se registrará en la auditoría y no cambiará los precios de venta.";
            ModeHint.Visibility = Visibility.Visible;
            SaveButton.Content = "Guardar costo";
        }
        if (!initialCostMode)
        {
            PriceTierBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Precio 1 · Principal", Tag = "Price1" });
            PriceTierBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = product.Price2.HasValue ? "Precio 2" : "Precio 2 · configurar por primera vez", Tag = "Price2" });
            PriceTierBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = product.Price3.HasValue ? "Precio 3" : "Precio 3 · configurar por primera vez", Tag = "Price3" });
            PriceTierBox.SelectedIndex = 0;
            UpdateCurrentPrice();
        }
        _initializing = false;
    }
    private void PriceTier_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateCurrentPrice();
    }
    private void UpdateCurrentPrice()
    {
        if (PriceTierBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item || item.Tag is not string tier) return;
        _priceTier = tier;
        var current = tier switch { "Price1" => _product.UnitPrice, "Price2" => _product.Price2, _ => _product.Price3 };
        CurrentPriceLabel.Text = current.HasValue ? $"Actual: {_product.Currency} {current.Value:N2}" : "Este nivel todavía no tiene precio asignado.";
        PriceBox.Text = current?.ToString("N2", PosNumberFormat.Culture) ?? string.Empty;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(PriceBox.Text, out var price) || price < 0m || decimal.Round(price, 4) != price)
        { ErrorLabel.Text = _initialCostMode ? "El costo debe ser no negativo y tener hasta cuatro decimales." : "El precio debe ser no negativo y tener hasta cuatro decimales."; return; }
        var reason = ReasonBox.Text.Trim();
        if (_initialCostMode && reason.Length is < 3 or > 250)
        { ErrorLabel.Text = "Indica un motivo o referencia de 3 a 250 caracteres."; return; }
        try
        {
            if (_initialCostMode) await _viewModel.InitializeProductCostAsync(_product.Id, price, reason);
            else await _viewModel.ChangePriceAsync(_product.Id, _priceTier, price, reason);
            DialogResult = true;
        }
        catch (Exception ex) { ErrorLabel.Text = PosUserMessage.ForException(ex); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
