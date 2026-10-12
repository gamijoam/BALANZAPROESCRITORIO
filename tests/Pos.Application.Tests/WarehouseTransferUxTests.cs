using System.Xml.Linq;
using Pos.Modules.Warehouses.Desktop;

namespace Pos.Application.Tests;

public sealed class WarehouseTransferUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Product_search_requires_an_explicit_selection_before_adding_to_the_transfer()
    {
        var view = LoadView();
        var results = FindNamed(view, "ProductSearchResults");
        Assert.Equal("ProductSearchResults_SelectionChanged", (string?)results.Attribute("SelectionChanged"));
        Assert.Equal("ProductSearchResults_PreviewKeyDown", (string?)results.Attribute("PreviewKeyDown"));
        Assert.Equal("TransferQuantityBox_KeyDown", (string?)FindNamed(view, "TransferQuantityBox").Attribute("KeyDown"));
        Assert.Equal("Buscar producto por código o nombre", (string?)FindNamed(view, "TransferProductBox").Attribute("AutomationProperties.Name"));
        Assert.Equal("BuscarTransferProducts_Click", (string?)FindNamed(view, "TransferProductSearchButton").Attribute("Click"));
        Assert.Equal("False", (string?)FindNamed(view, "AddTransferLineButton").Attribute("IsEnabled"));
        Assert.NotNull(FindNamed(view, "SelectedTransferProductName"));
        Assert.NotNull(FindNamed(view, "SelectedTransferProductSourceStock"));

        var code = LoadSource();
        Assert.Contains("SearchTransferProductsAsync", code);
        Assert.Contains("SelectTransferProduct", code);
        Assert.Contains("_selectedTransferProduct", code);
        Assert.Contains("_transferSourceStockWarehouseId == activeSource.Id", code);
        Assert.DoesNotContain("products.FirstOrDefault()", code);
        Assert.DoesNotContain("?? products.FirstOrDefault()", code);
    }

    [Fact]
    public void Transfer_status_is_outside_tabs_and_visible_on_the_active_view()
    {
        var view = LoadView();
        var status = FindNamed(view, "WarehouseStatus");

        Assert.Empty(status.Ancestors(Presentation + "TabItem"));
        Assert.Equal("Wrap", (string?)status.Attribute("TextWrapping"));
        Assert.Equal("TransferStatus", (string?)status.Attribute("AutomationProperties.AutomationId"));
    }

    [Fact]
    public void Transfer_list_selection_loads_and_displays_snapshotted_line_details()
    {
        var view = LoadView();
        var transfers = FindNamed(view, "TransfersGrid");
        Assert.Equal("TransfersGrid_SelectionChanged", (string?)transfers.Attribute("SelectionChanged"));
        Assert.NotNull(FindNamed(view, "TransferDetailsGrid"));

        var desktop = LoadSource();
        Assert.Contains("TransfersGrid_SelectionChanged", desktop);
        Assert.Contains("LoadSelectedTransferDetailsAsync", desktop);
        Assert.Contains("public sealed record WarehouseTransferDetailsClientInfo", desktop);
        Assert.Contains("LoadTransfersAsync(created?.Id)", desktop);
        Assert.Contains("TransferQuantityBox_KeyDown", desktop);

        var server = File.ReadAllText(ReadRepositoryFile("src", "Modules", "Pos.Modules.Warehouses", "WarehouseModule.cs"));
        Assert.Contains("transfers/{id:guid}", server);
        Assert.Contains("GetTransferDetailsAsync", server);
        Assert.Contains("\"Warehouse.View\"", server);
        Assert.Contains("sku_snapshot,name_snapshot,quantity,received_quantity,variance", server);
        Assert.Contains("Warehouse.Transfer", File.ReadAllText(ReadRepositoryFile("src", "Modules", "Pos.Modules.Warehouses", "module.manifest.json")));
    }

    [Fact]
    public void Transfer_controls_have_accessible_names_and_explicit_keyboard_selection()
    {
        var view = LoadView();
        foreach (var name in new[] { "TransferSourcePicker", "TransferDestinationPicker", "TransferProductBox", "TransferProductSearchButton", "ProductSearchResults", "TransferQuantityBox", "AddTransferLineButton", "CreateTransferButton", "TransfersGrid", "TransferDetailsGrid" })
            Assert.False(string.IsNullOrWhiteSpace((string?)FindNamed(view, name).Attribute("AutomationProperties.Name")), $"Falta nombre accesible: {name}");

        var source = LoadSource();
        Assert.Contains("if (e.Key != Key.Enter || ProductSearchResults.SelectedItem", source);
        Assert.Contains("ProductSearchResults.SelectedItem is ProductInfo", source);
        Assert.Contains("TransferSourcePicker_SelectionChanged", source);
    }

    [Fact]
    public void Warehouse_quantities_use_the_POS_culture_without_grouping_whole_units_as_thousands()
    {
        var view = LoadView();
        Assert.Equal("es-VE", (string?)view.Root?.Attribute("Language"));
        Assert.All(view.Descendants(Presentation + "DataGridTextColumn")
                .Where(column => ((string?)column.Attribute("Header")) is "Cantidad" or "Unid." or "Enviado" or "Recibido" or "Diferencia"),
            column => Assert.Contains("WarehouseQuantityConverter", (string?)column.Attribute("Binding")));
        Assert.DoesNotContain(view.Descendants(Presentation + "DataGridTextColumn"), column =>
            ((string?)column.Attribute("Binding"))?.Contains("StringFormat=N3", StringComparison.Ordinal) == true);
        Assert.Contains("PosNumberFormat.FormatQuantity", LoadSource());
        Assert.Contains("WarehouseQuantityConverter.Instance", LoadSource());
        Assert.Contains("WarehouseAmountConverter.Instance", LoadSource());
    }

    [Theory]
    [InlineData(90d, "90")]
    [InlineData(90.25d, "90,25")]
    [InlineData(0.5d, "0,5")]
    public void Quantity_converter_displays_decimal_quantity_without_thousands_ambiguity(double value, string expected)
    {
        var actual = WarehouseQuantityConverter.Instance.Convert((decimal)value, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Warehouse_stock_view_labels_price_currency_and_formats_its_amount()
    {
        var source = LoadSource();
        Assert.Contains("Header = \"Precio\"", source);
        Assert.Contains("Header = \"Moneda\"", source);
        Assert.Contains("WarehouseAmountConverter.Instance", source);
        Assert.Contains("PosNumberFormat.FormatAmount(amount)", source);
    }

    [Fact]
    public void New_transfer_uses_a_compact_catalog_and_draft_split_with_visible_stock_context()
    {
        var view = LoadView();
        var composer = FindNamed(view, "TransferComposer");
        Assert.Equal(2, composer.Elements(Presentation + "Grid.ColumnDefinitions").Single()
            .Elements(Presentation + "ColumnDefinition").Count());
        Assert.Contains("Header = \"Existencia\"", LoadSource());
        Assert.NotNull(FindNamed(view, "TransferDraftGrid"));
        Assert.NotNull(FindNamed(view, "CreateTransferButton"));
        Assert.Equal("1", (string?)FindNamed(view, "TransferDraftGrid").Ancestors(Presentation + "GroupBox").First().Attribute("Grid.Column"));
        Assert.Equal("0", (string?)FindNamed(view, "TransferCatalogPanel").Attribute("Grid.Column"));
    }

    private static XElement FindNamed(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XDocument LoadView() => XDocument.Load(ReadRepositoryFile("src", "Modules", "Pos.Modules.Warehouses.Desktop", "WarehouseWindow.xaml"));
    private static string LoadSource() => File.ReadAllText(ReadRepositoryFile("src", "Modules", "Pos.Modules.Warehouses.Desktop", "WarehouseWindow.xaml.cs"));

    private static string ReadRepositoryFile(params string[] paths)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. paths]);
    }
}
