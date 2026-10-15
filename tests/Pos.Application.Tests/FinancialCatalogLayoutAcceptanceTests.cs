using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class FinancialCatalogLayoutAcceptanceTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Product_creation_requires_tax_treatment_and_offers_optional_price_tiers()
    {
        var form = Load("ProductCreateWindow.xaml");

        Assert.Contains(form.Descendants(Ui + "ComboBox"), x => (string?)x.Attribute(Xaml + "Name") == "TaxTreatmentBox");
        Assert.Contains(form.Descendants(Ui + "TextBox"), x => (string?)x.Attribute(Xaml + "Name") == "Price2Box");
        Assert.Contains(form.Descendants(Ui + "TextBox"), x => (string?)x.Attribute(Xaml + "Name") == "Price3Box");
        Assert.Contains(form.Descendants(Ui + "TextBlock"), x => ((string?)x.Attribute("Text"))?.Contains("Impuesto", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(form.Descendants(Ui + "ComboBoxItem"), x => Equals((string?)x.Attribute("Tag"), "Taxable16"));
        Assert.Contains(form.Descendants(Ui + "ComboBoxItem"), x => Equals((string?)x.Attribute("Tag"), "Taxable8"));
        Assert.Contains(form.Descendants(Ui + "ComboBoxItem"), x => Equals((string?)x.Attribute("Tag"), "Taxable0"));
        Assert.Contains(form.Descendants(Ui + "ComboBoxItem"), x => Equals((string?)x.Attribute("Tag"), "Exempt"));
    }

    [Fact]
    public void Cart_line_editor_allows_only_configured_price_tiers_and_keeps_manual_price_reason()
    {
        var form = Load("CartLineEditWindow.xaml");

        Assert.Contains(form.Descendants(Ui + "ComboBox"), x => (string?)x.Attribute(Xaml + "Name") == "PriceTierBox");
        Assert.Contains(form.Descendants(Ui + "TextBox"), x => (string?)x.Attribute(Xaml + "Name") == "PriceOverrideReasonBox");
        Assert.Contains(form.Descendants(Ui + "TextBlock"), x => ((string?)x.Attribute("Text"))?.Contains("Precio 2", StringComparison.Ordinal) == true);
        Assert.Contains(form.Descendants(Ui + "TextBlock"), x => ((string?)x.Attribute("Text"))?.Contains("Precio 3", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Inventory_exposes_audited_initial_cost_for_unvalued_stock_and_suggestions_explain_the_action()
    {
        var inventory = Load("InventoryWindow.xaml");
        Assert.Contains(inventory.Descendants(Ui + "Button"), x => (string?)x.Attribute(Xaml + "Name") == "InitializeCostButton");

        var root = FindRepositoryRoot();
        var inventoryCode = File.ReadAllText(Path.Combine(root.FullName, "src", "Pos.Desktop", "InventoryWindow.xaml.cs"));
        var priceWindowCode = File.ReadAllText(Path.Combine(root.FullName, "src", "Pos.Desktop", "ProductPriceWindow.xaml.cs"));
        Assert.Contains("initialCostMode: true", inventoryCode);
        Assert.Contains("InitializeProductCostAsync", priceWindowCode);

        var suggestions = File.ReadAllText(Path.Combine(root.FullName, "src", "Modules", "Pos.Modules.Finance.Desktop", "PriceSuggestionWindow.xaml.cs"));
        Assert.Contains("asigna el costo inicial desde Inventario", suggestions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inventory_has_a_visible_action_to_classify_legacy_products()
    {
        var form = Load("InventoryWindow.xaml");
        Assert.Contains(form.Descendants(Ui + "Button"), x => (string?)x.Attribute(Xaml + "Name") == "ChangeTaxTreatmentButton");
        Assert.Contains(form.Descendants(Ui + "DataGridTextColumn"), x => ((string?)x.Attribute("Binding"))?.Contains("TaxTreatmentLabel", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Inventory_window_uses_available_screen_space_and_keeps_product_actions_scrollable()
    {
        var form = Load("InventoryWindow.xaml");
        var window = Assert.IsType<XElement>(form.Root);
        Assert.True(double.Parse((string)window.Attribute("Width")!, System.Globalization.CultureInfo.InvariantCulture) >= 1450);
        Assert.True(double.Parse((string)window.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture) >= 820);
        Assert.Contains(form.Descendants(Ui + "ScrollViewer"), x => (string?)x.Attribute(Xaml + "Name") == "ProductDetailsScrollViewer");

        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "src", "Pos.Desktop", "InventoryWindow.xaml.cs"));
        Assert.Contains("SystemParameters.WorkArea", code);
    }

    [Fact]
    public void Supplier_payment_window_is_larger_and_keeps_confirm_actions_visible_when_form_scrolls()
    {
        var form = LoadFinance("SupplierPaymentWindow.xaml");
        var window = Assert.IsType<XElement>(form.Root);
        Assert.True(double.Parse((string)window.Attribute("Width")!, System.Globalization.CultureInfo.InvariantCulture) >= 820);
        Assert.True(double.Parse((string)window.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture) >= 720);
        Assert.Contains(form.Descendants(Ui + "ScrollViewer"), x => (string?)x.Attribute(Xaml + "Name") == "PaymentFormScrollViewer");
        var footer = Assert.Single(form.Descendants(Ui + "Grid"), x => (string?)x.Attribute("Grid.Row") == "3");
        Assert.Contains(footer.Descendants(Ui + "Button"), x => (string?)x.Attribute(Xaml + "Name") == "ConfirmPaymentButton");
        Assert.Contains(footer.Descendants(Ui + "Button"), x => (string?)x.Attribute(Xaml + "Name") == "CancelPaymentButton");

        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "src", "Modules", "Pos.Modules.Finance.Desktop", "SupplierPaymentWindow.xaml.cs"));
        Assert.Contains("SystemParameters.WorkArea", code);
    }

    [Fact]
    public void Supplier_receipt_window_uses_available_screen_space_and_keeps_confirmation_outside_the_grid()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root.FullName, "src", "Modules", "Pos.Modules.Finance.Desktop", "FinanceWindow.xaml.cs"));

        Assert.Contains("Width = 1160; Height = 760", code);
        Assert.Contains("SystemParameters.WorkArea", code);
        Assert.Contains("Grid.SetRow(actions, 2)", code);
        Assert.Contains("workArea.Height - 48", code);
    }

    [Fact]
    public void Product_search_disables_sale_for_unclassified_legacy_items_and_shows_why()
    {
        var form = Load("ProductSelectionWindow.xaml");
        Assert.Contains(form.Descendants(), x => ((string?)x.Attribute("Binding"))?.Contains("Product.IsTaxClassified", StringComparison.Ordinal) == true);
        Assert.Contains(form.Descendants(Ui + "TextBlock"), x => ((string?)x.Attribute("Text"))?.Contains("Product.TaxTreatmentLabel", StringComparison.Ordinal) == true ||
            ((string?)x.Attribute("Text"))?.Contains("{Binding Product.TaxTreatmentLabel}", StringComparison.Ordinal) == true ||
            x.ToString().Contains("Product.TaxTreatmentLabel", StringComparison.Ordinal));
    }

    private static XDocument Load(string fileName)
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot().FullName, "src", "Pos.Desktop", fileName));
    }

    private static XDocument LoadFinance(string fileName)
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot().FullName, "src", "Modules", "Pos.Modules.Finance.Desktop", fileName));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!;
    }
}
