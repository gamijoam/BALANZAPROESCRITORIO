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
        var productDetails = Assert.Single(document.Descendants(Presentation + "ScrollViewer"),
            x => (string?)x.Attribute(Xaml + "Name") == "ProductDetailsScrollViewer");
        Assert.Equal("1", (string?)productDetails.Attribute("Grid.Column"));
        Assert.Contains(productDetails.Descendants(Presentation + "Border"), border =>
            border.Descendants(Presentation + "Button").Contains(button));
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
    public void Inventory_applies_stock_filter_after_module_extensions_update_quantities()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var code = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "InventoryWindow.xaml.cs"));

        var inventoryRequest = code.IndexOf("GetInventoryAsync(FilterQueryBox.Text, status, currency, \"\", hasImage)", StringComparison.Ordinal);
        var extensionApplication = code.IndexOf("extension.ApplyFilterAsync", StringComparison.Ordinal);
        var stockFiltering = code.IndexOf("InventoryStockFilter.Apply", StringComparison.Ordinal);

        Assert.True(inventoryRequest >= 0, "The host must fetch catalog rows without pre-filtering on global stock.");
        Assert.True(extensionApplication > inventoryRequest, "Module filters must update rows before stock availability is evaluated.");
        Assert.True(stockFiltering > extensionApplication, "Availability must be evaluated against the displayed quantity.");
    }

    [Fact]
    public void Inventory_places_stock_adjustment_in_a_full_width_bar_separate_from_product_actions()
    {
        var document = LoadInventoryView();
        var delta = Assert.Single(document.Descendants(Presentation + "TextBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "DeltaBox");
        var reason = Assert.Single(document.Descendants(Presentation + "TextBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "ReasonBox");
        var grid = Assert.Single(document.Descendants(Presentation + "DataGrid"),
            x => (string?)x.Attribute(Xaml + "Name") == "InventoryGrid");

        Assert.Contains(delta.Ancestors(Presentation + "Border"), x => (string?)x.Attribute("Grid.Row") == "4");
        Assert.Contains(reason.Ancestors(Presentation + "Border"), x => (string?)x.Attribute("Grid.Row") == "4");
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        var adjustmentBar = Assert.Single(document.Descendants(Presentation + "Border"), x =>
            (string?)x.Attribute("Grid.Row") == "4" && x.Descendants(Presentation + "TextBox").Any());
        Assert.Contains(adjustmentBar.Descendants(Presentation + "Button"), x =>
            (string?)x.Attribute(Xaml + "Name") == "AdjustButton");
        var productPanel = Assert.Single(document.Descendants(Presentation + "ScrollViewer"), x =>
            (string?)x.Attribute(Xaml + "Name") == "ProductDetailsScrollViewer");
        Assert.Equal("1", (string?)productPanel.Attribute("Grid.Column"));
        Assert.DoesNotContain(productPanel.Descendants(Presentation + "TextBox"), x =>
            (string?)x.Attribute(Xaml + "Name") is "DeltaBox" or "ReasonBox");
    }

    [Fact]
    public void Inventory_history_window_uses_available_screen_space()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var form = XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml"));
        var window = Assert.IsType<XElement>(form.Root);
        Assert.True(double.Parse((string)window.Attribute("Width")!, System.Globalization.CultureInfo.InvariantCulture) >= 1250);
        Assert.True(double.Parse((string)window.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture) >= 740);

        var code = File.ReadAllText(Path.Combine(directory.FullName, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml.cs"));
        Assert.Contains("SystemParameters.WorkArea", code);
    }

    [Fact]
    public void Product_creation_explains_initial_stock_as_units_and_groups_fields_in_columns()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var form = XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "ProductCreateWindow.xaml"));

        Assert.Contains(form.Descendants(Presentation + "TextBlock"), x =>
            ((string?)x.Attribute("Text"))?.Contains("Existencia inicial (unidades)", StringComparison.Ordinal) == true);
        Assert.Contains(form.Descendants(Presentation + "Grid"), x => x.Elements(Presentation + "Grid.ColumnDefinitions").Any());
    }

    [Fact]
    public void Warehouse_module_maps_selected_warehouse_balances_into_inventory_rows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var code = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Modules", "Pos.Modules.Warehouses.Desktop", "WarehousesDesktopModule.cs"));

        Assert.Contains("WarehouseInventoryFilter.Apply", code, StringComparison.Ordinal);
        Assert.DoesNotContain("products.Where(product => ids.Contains(product.Id))", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmed_sales_write_immutable_stock_movements_with_actor_and_terminal()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var code = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Pos.Infrastructure", "Sales", "SalesService.cs"));

        Assert.Contains("db.StockMovements.Add(new StockMovementRecord", code, StringComparison.Ordinal);
        Assert.Contains("QuantityDelta = -line.Quantity", code, StringComparison.Ordinal);
        Assert.Contains("ActorId = operatorId", code, StringComparison.Ordinal);
        Assert.Contains("TerminalId = terminalId", code, StringComparison.Ordinal);
        Assert.Contains("Reason = $\"Venta {saleId:N}\"", code, StringComparison.Ordinal);
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
