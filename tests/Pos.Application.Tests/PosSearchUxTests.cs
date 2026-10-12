using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class PosSearchUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Product_search_rows_show_a_thumbnail_and_available_price_details()
    {
        var selection = LoadView("ProductSelectionWindow.xaml");
        var list = Assert.Single(selection.Descendants(Presentation + "ListBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "ProductsList");
        var template = Assert.Single(list.Descendants(Presentation + "DataTemplate"));
        Assert.Contains(template.Descendants(Presentation + "Image"), image =>
            ((string?)image.Attribute("Source"))?.Contains("ImageSource", StringComparison.Ordinal) == true);
        Assert.Contains(template.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding Product.Sku}");
        Assert.Contains(template.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding Product.Name}");
        Assert.Contains(template.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding Product.PriceLabel}");
        Assert.Contains("↑ y ↓", string.Join(" ", selection.Descendants(Presentation + "TextBlock").Attributes("Text").Select(x => x.Value)));
        Assert.Contains("Enter", string.Join(" ", selection.Descendants(Presentation + "TextBlock").Attributes("Text").Select(x => x.Value)));
    }

    [Fact]
    public void Added_product_summary_shows_code_name_and_price_and_selects_its_cart_row()
    {
        var main = LoadView("MainWindow.xaml");
        Assert.Contains(main.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute(Xaml + "Name") == "ProductSummaryCode");
        Assert.Contains(main.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute(Xaml + "Name") == "ProductSummaryName");
        Assert.Contains(main.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute(Xaml + "Name") == "ProductSummaryPrice");

        var code = LoadSource("MainWindow.xaml.cs");
        Assert.Contains("CartGrid.SelectedItem = selectedLine", code);
        Assert.Contains("CartGrid.ScrollIntoView(selectedLine)", code);
        Assert.Contains("ProductPreviewSku.Text", code);
        Assert.Contains("ProductPreviewPrice.Text", code);
        Assert.Contains("nameof(PosViewModel.CurrentPriceLabel)", code);
    }

    [Fact]
    public void Checkout_total_card_keeps_title_and_reference_on_one_line_above_the_amount()
    {
        var main = LoadView("MainWindow.xaml");
        var card = Assert.Single(main.Descendants(Presentation + "Border"),
            x => (string?)x.Attribute(Xaml + "Name") == "TotalCard");
        var labels = Assert.Single(card.Descendants(Presentation + "Grid"),
            x => (string?)x.Attribute(Xaml + "Name") == "TotalCardLabels");

        Assert.Contains(labels.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute("Text") == "TOTAL A PAGAR");
        Assert.Contains(labels.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute("Text") == "{Binding ReferenceTotalLabel}");
        Assert.Contains(card.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute("Text") == "{Binding Total, StringFormat={}{0:N2}}");
        Assert.DoesNotContain(card.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute("Text") == "TOTAL A PAGAR" && x.Parent?.Name.LocalName == "StackPanel");
    }

    [Fact]
    public void Product_selection_returns_the_product_from_its_image_card()
    {
        var code = LoadSource("ProductSelectionWindow.xaml.cs");
        Assert.Contains("SelectedItem as ProductSelectionEntry", code);
        Assert.Contains("GetProductImageAsync", LoadSource("MainWindow.xaml.cs"));
    }

    private static XDocument LoadView(string name) => XDocument.Load(Path.Combine(FindRoot().FullName, "src", "Pos.Desktop", name));
    private static string LoadSource(string name) => File.ReadAllText(Path.Combine(FindRoot().FullName, "src", "Pos.Desktop", name));

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}
