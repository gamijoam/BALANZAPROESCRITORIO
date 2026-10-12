using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class InventoryLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Product_photo_is_fitted_without_clipping_its_viewport()
    {
        var document = LoadInventoryView();
        var image = Assert.Single(document.Descendants(Presentation + "Image"));
        Assert.Equal("Uniform", (string?)image.Attribute("Stretch"));
        var viewport = image.Ancestors(Presentation + "Border").First();
        Assert.NotEqual("True", (string?)viewport.Attribute("ClipToBounds"));
    }

    [Fact]
    public void Change_price_action_is_in_the_selected_product_panel()
    {
        var document = LoadInventoryView();
        var button = Assert.Single(document.Descendants(Presentation + "Button"),
            x => (string?)x.Attribute(Xaml + "Name") == "ChangePriceButton");
        Assert.Contains(button.Ancestors(Presentation + "Border"), x => (string?)x.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void Inventory_exposes_an_optional_extension_slot_without_hardcoding_warehouse_filter()
    {
        var document = LoadInventoryView();
        var extensionSlot = Assert.Single(document.Descendants(Presentation + "WrapPanel"),
            x => (string?)x.Attribute(Xaml + "Name") == "InventoryExtensionsPanel");

        Assert.Equal("2", (string?)extensionSlot.Attribute("Grid.Row"));
        Assert.DoesNotContain(document.Descendants(Presentation + "ComboBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "WarehouseFilter");
    }

    [Fact]
    public void Product_deactivation_clears_the_bottom_status_instead_of_showing_a_legend()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var code = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "InventoryWindow.xaml.cs"));
        var handler = code[code.IndexOf("private async void ToggleProductActive_Click", StringComparison.Ordinal)..];
        Assert.Contains("StatusLabel.Text = string.Empty", handler);
        Assert.DoesNotContain("Producto desactivado; conserva su historial", handler);
    }

    private static XDocument LoadInventoryView()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "InventoryWindow.xaml"));
    }
}
