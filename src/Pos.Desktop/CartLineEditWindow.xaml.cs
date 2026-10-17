using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CartLineEditWindow : Window
{
    private readonly CartLineViewModel _line;
    private ProductSaleUnitInfo _selectedSaleUnit;
    private bool _initializing = true;

    public decimal Quantity { get; private set; }
    public ProductSaleUnitInfo SaleUnit { get; private set; }
    public string PriceTier { get; private set; } = "Price1";
    public decimal? UnitPriceOverride { get; private set; }
    public string? PriceOverrideReason { get; private set; }

    public CartLineEditWindow(CartLineViewModel line)
    {
        _line = line;
        InitializeComponent();
        ProductLabel.Text = $"{line.Product.Name} · Código {line.Product.Sku}";
        IReadOnlyList<ProductSaleUnitInfo> saleUnits = line.AvailableSaleUnits;
        SaleUnitBox.ItemsSource = saleUnits;
        _selectedSaleUnit = saleUnits.FirstOrDefault(x => SameSaleUnit(x, line.SaleUnit)) ?? saleUnits[0];
        SaleUnit = _selectedSaleUnit;
        SaleUnitBox.SelectedItem = _selectedSaleUnit;
        UpdateUnitHint(_selectedSaleUnit);
        QuantityBox.Text = line.Quantity.ToString("0.###", PosNumberFormat.Culture);
        PopulatePriceTiers(_selectedSaleUnit, line.PriceTier);
        PriceOverrideBox.Text = line.UnitPrice.ToString("N2", PosNumberFormat.Culture);
        PriceOverrideReasonBox.Text = line.PriceOverrideReason ?? string.Empty;
        UpdateCatalogLabels();
        _initializing = false;
        Loaded += (_, _) => QuantityBox.Focus();
    }

    private static bool SameSaleUnit(ProductSaleUnitInfo left, ProductSaleUnitInfo right)
        => left.PresentationId == right.PresentationId && left.UnitId == right.UnitId;

    private void PopulatePriceTiers(ProductSaleUnitInfo saleUnit, string requestedTier)
    {
        PriceTierBox.Items.Clear();
        var tiers = new List<string> { "Price1" };
        if (saleUnit.Price2.HasValue) tiers.Add("Price2");
        if (saleUnit.Price3.HasValue) tiers.Add("Price3");
        foreach (var tier in tiers)
            PriceTierBox.Items.Add(new ComboBoxItem
            {
                Content = tier switch { "Price1" => "Precio 1 · Principal", "Price2" => "Precio 2", _ => "Precio 3" },
                Tag = tier
            });
        var selectedTier = tiers.Contains(requestedTier, StringComparer.Ordinal) ? requestedTier : "Price1";
        PriceTierBox.SelectedItem = PriceTierBox.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, selectedTier));
        PriceTier = selectedTier;
    }

    private void UpdateUnitHint(ProductSaleUnitInfo saleUnit)
        => UnitHintLabel.Text = $"1 {saleUnit.Label} descuenta {PosNumberFormat.FormatQuantity(saleUnit.ConversionFactor)} { _line.Product.BaseUnitAbbreviation} del inventario.";

    private void SaleUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SaleUnitBox.SelectedItem is not ProductSaleUnitInfo saleUnit || SameSaleUnit(saleUnit, _selectedSaleUnit)) return;

        _initializing = true;
        _selectedSaleUnit = saleUnit;
        SaleUnit = saleUnit;
        PopulatePriceTiers(saleUnit, PriceTier);
        _initializing = false;
        PriceOverrideBox.Text = _line.CatalogUnitPriceFor(saleUnit, PriceTier).ToString("N2", PosNumberFormat.Culture);
        PriceOverrideReasonBox.Clear();
        UpdateUnitHint(saleUnit);
        UpdateCatalogLabels();
    }

    private void PriceTier_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PriceTierBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tier) return;
        PriceTier = tier;
        PriceOverrideBox.Text = _line.CatalogUnitPriceFor(_selectedSaleUnit, tier).ToString("N2", PosNumberFormat.Culture);
        PriceOverrideReasonBox.Clear();
        UpdateCatalogLabels();
    }

    private void UpdateCatalogLabels()
    {
        var catalogPrice = _line.CatalogUnitPriceFor(_selectedSaleUnit, PriceTier);
        CatalogPriceLabel.Text = $"Catálogo · {PriceTier.Replace("Price", "Precio ")}: {_line.SettlementCurrency} {catalogPrice:N2}. El nivel se guarda con la venta.";
        TaxPreviewLabel.Text = $"Impuesto: {_line.Product.TaxTreatmentLabel} · {_line.TaxRate:P0}. El total de línea lo incluye.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = string.Empty;
        if (!decimal.TryParse(QuantityBox.Text.Trim(), NumberStyles.Number, PosNumberFormat.Culture, out var quantity) ||
            quantity <= 0m || decimal.Round(quantity, 3) != quantity || !_selectedSaleUnit.AllowsFractionalQuantities && decimal.Round(quantity, 0) != quantity)
        {
            ErrorLabel.Text = _selectedSaleUnit.AllowsFractionalQuantities
                ? "La cantidad debe ser mayor que cero y tener hasta tres decimales."
                : $"La cantidad de {_selectedSaleUnit.Label} debe ser un número entero.";
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
        if (price == _line.CatalogUnitPriceFor(_selectedSaleUnit, PriceTier))
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
        SaleUnit = _selectedSaleUnit;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
