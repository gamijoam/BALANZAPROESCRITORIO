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
    public void Inventory_has_a_visible_action_to_classify_legacy_products()
    {
        var form = Load("InventoryWindow.xaml");
        Assert.Contains(form.Descendants(Ui + "Button"), x => (string?)x.Attribute(Xaml + "Name") == "ChangeTaxTreatmentButton");
        Assert.Contains(form.Descendants(Ui + "DataGridTextColumn"), x => ((string?)x.Attribute("Binding"))?.Contains("TaxTreatmentLabel", StringComparison.Ordinal) == true);
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", fileName));
    }
}
