using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CartLineEditWindow : Window
{
    private readonly CartLineViewModel _line;
    private bool _initializing = true;
    public decimal Quantity { get; private set; }
    public string PriceTier { get; private set; } = "Price1";
    public decimal? UnitPriceOverride { get; private set; }
    public string? PriceOverrideReason { get; private set; }

    public CartLineEditWindow(CartLineViewModel line)
    {
        _line = line;
        InitializeComponent();
        ProductLabel.Text = $"{line.Product.Name} · Código {line.Product.Sku}";
        QuantityBox.Text = line.Quantity.ToString("0.###", PosNumberFormat.Culture);
        foreach (var tier in line.AvailablePriceTiers)
            PriceTierBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = tier switch { "Price1" => "Precio 1 · Principal", "Price2" => "Precio 2", _ => "Precio 3" }, Tag = tier });
        PriceTierBox.SelectedItem = PriceTierBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().First(x => Equals(x.Tag, line.PriceTier));
        PriceTier = line.PriceTier;
        PriceOverrideBox.Text = line.UnitPrice.ToString("N2", PosNumberFormat.Culture);
        PriceOverrideReasonBox.Text = line.PriceOverrideReason ?? string.Empty;
        UpdateCatalogLabels();
        _initializing = false;
        Loaded += (_, _) => QuantityBox.Focus();
    }

    private void PriceTier_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || PriceTierBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item || item.Tag is not string tier) return;
        PriceTier = tier;
        PriceOverrideBox.Text = _line.CatalogUnitPriceFor(tier).ToString("N2", PosNumberFormat.Culture);
        PriceOverrideReasonBox.Clear();
        UpdateCatalogLabels();
    }

    private void UpdateCatalogLabels()
    {
        var catalogPrice = _line.CatalogUnitPriceFor(PriceTier);
        CatalogPriceLabel.Text = $"Catálogo · {PriceTier.Replace("Price", "Precio ")}: {_line.SettlementCurrency} {catalogPrice:N2}. El nivel se guarda con la venta.";
        TaxPreviewLabel.Text = $"Impuesto: {_line.Product.TaxTreatmentLabel} · {_line.TaxRate:P0}. El total de línea lo incluye.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = string.Empty;
        if (!decimal.TryParse(QuantityBox.Text.Trim(), NumberStyles.Number, PosNumberFormat.Culture, out var quantity) ||
            quantity <= 0m || decimal.Round(quantity, 3) != quantity)
        {
            ErrorLabel.Text = "La cantidad debe ser mayor que cero y tener hasta tres decimales.";
            QuantityBox.Focus();
            QuantityBox.SelectAll();
            return;
        }

        if (!PosNumberFormat.TryParseAmount(PriceOverrideBox.Text, out var price) || price <= 0m || decimal.Round(price, 4) != price)
        {
            ErrorLabel.Text = "El precio debe ser mayor que cero y tener hasta cuatro decimales.";
            PriceOverrideBox.Focus();
            PriceOverrideBox.SelectAll();
            return;
        }

        var reason = PriceOverrideReasonBox.Text.Trim();
        // Typing the catalog price means “use the catalog price”, so no special authorization is needed.
        if (price == _line.CatalogUnitPriceFor(PriceTier))
        {
            UnitPriceOverride = null;
            PriceOverrideReason = null;
        }
        else
        {
            if (reason.Length is < 3 or > 250)
            {
                ErrorLabel.Text = "Escribe un motivo de entre 3 y 250 caracteres para el precio especial.";
                PriceOverrideReasonBox.Focus();
                return;
            }
            UnitPriceOverride = price;
            PriceOverrideReason = reason;
        }

        Quantity = quantity;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
