using System.Xml.Linq;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class ProductCustomerReturnsUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void New_product_price_starts_empty()
    {
        var document = LoadView("ProductCreateWindow.xaml");
        var price = Assert.Single(document.Descendants(Presentation + "TextBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "PriceBox");
        Assert.True(string.IsNullOrEmpty((string?)price.Attribute("Text")));
    }

    [Fact]
    public void New_product_form_exposes_an_optional_initial_cost_in_the_selected_product_currency()
    {
        var document = LoadView("ProductCreateWindow.xaml");
        var cost = Assert.Single(document.Descendants(Presentation + "TextBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "InitialCostBox");
        Assert.True(string.IsNullOrEmpty((string?)cost.Attribute("Text")));
        Assert.Contains("Costo inicial", document.ToString(), StringComparison.OrdinalIgnoreCase);

        var source = LoadSource("ProductCreateWindow.xaml.cs");
        Assert.Contains("CreateProductAsync", source);
        Assert.Contains("InitialCostBox.Text", source);
    }

    [Fact]
    public void Successful_customer_creation_does_not_leave_a_bottom_success_legend()
    {
        var source = LoadSource("CustomersWindow.xaml.cs");
        Assert.Contains("StatusLabel.Text = string.Empty", source);
        Assert.DoesNotContain("StatusLabel.Text = \"Cliente creado.", source);
    }

    [Fact]
    public void New_customer_credit_limit_starts_empty()
    {
        var document = LoadView("CustomersWindow.xaml");
        var limit = Assert.Single(document.Descendants(Presentation + "TextBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "LimitBox");
        Assert.True(string.IsNullOrEmpty((string?)limit.Attribute("Text")));
        var source = LoadSource("CustomersWindow.xaml.cs");
        Assert.Contains("LimitBox.Clear()", source);
        Assert.DoesNotContain("LimitBox.Text = \"0\"", source);
    }

    [Fact]
    public void Successful_customer_removal_does_not_leave_a_bottom_success_legend()
    {
        var source = LoadSource("CustomersWindow.xaml.cs");
        var handler = source[source.IndexOf("private async void DeleteCustomer_Click", StringComparison.Ordinal)..];
        Assert.Contains("StatusLabel.Text = string.Empty", handler);
        Assert.DoesNotContain("StatusLabel.Text = \"Cliente eliminado", handler);
    }

    [Theory]
    [InlineData("Confirmed", "Confirmada")]
    [InlineData("PartiallyReturned", "Devuelta parcialmente")]
    [InlineData("Returned", "Devuelta")]
    [InlineData("Voided", "Anulada")]
    public void Sale_status_is_presented_in_spanish(string status, string expected)
    {
        var row = new SaleDetailRow(Guid.NewGuid(), DateTime.UtcNow, 10m, "USD", "operador", "venta general", Guid.NewGuid(), [], [])
        { Status = status };

        Assert.Equal(expected, row.StatusLabel);
    }

    [Fact]
    public void Sale_detail_marks_returned_product_rows_and_quantities()
    {
        var line = new SaleDetailLine("A-1", "Producto", 3m, 5m, 15m, "USD", 5m) { ReturnedQuantity = 1m };

        Assert.True(line.HasReturnedUnits);
        Assert.Equal("Devuelto parcialmente", line.ReturnStatusLabel);
        Assert.Equal("Sin devolución", new SaleDetailLine("B-2", "Otro", 2m, 3m, 6m, "USD", 3m).ReturnStatusLabel);

        var document = LoadView("ManagementWindow.xaml");
        var grid = Assert.Single(document.Descendants(Presentation + "DataGrid"),
            x => (string?)x.Attribute(Xaml + "Name") == "SaleLinesGrid");
        var columns = grid.Descendants(Presentation + "DataGridTextColumn").ToArray();
        Assert.Contains(columns, x => (string?)x.Attribute("Header") == "Devueltos" &&
            ((string?)x.Attribute("Binding"))?.Contains("ReturnedQuantity", StringComparison.Ordinal) == true);
        Assert.Contains(columns, x => (string?)x.Attribute("Header") == "Estado de devolución" &&
            ((string?)x.Attribute("Binding"))?.Contains("ReturnStatusLabel", StringComparison.Ordinal) == true);
        Assert.Contains(grid.Elements(Presentation + "DataGrid.RowStyle"), rowStyle =>
            rowStyle.Descendants(Presentation + "DataTrigger").Any(trigger =>
                (string?)trigger.Attribute("Binding") == "{Binding HasReturnedUnits}"));
    }

    private static XDocument LoadView(string fileName)
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine(root.FullName, "src", "Pos.Desktop", fileName));
    }

    private static string LoadSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "src", "Pos.Desktop", fileName));

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}

public sealed class CashOpeningCurrencyUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Cash_opening_does_not_ask_for_the_primary_currency_and_always_uses_ves()
    {
        var root = FindRepositoryRoot();
        var view = XDocument.Load(Path.Combine(root.FullName, "src", "Pos.Desktop", "OpenCashWindow.xaml"));
        var text = string.Join(" ", view.Descendants().Attributes("Text").Select(x => x.Value));
        Assert.DoesNotContain("Moneda principal de la venta", text);
        Assert.DoesNotContain(view.Descendants(Presentation + "ComboBox"),
            x => (string?)x.Attribute(Xaml + "Name") == "CurrencyBox");
        Assert.Contains(view.Descendants(Presentation + "TextBlock"),
            x => ((string?)x.Attribute("Text"))?.Contains("USD", StringComparison.Ordinal) == true);
        Assert.Contains(view.Descendants(Presentation + "TextBlock"),
            x => ((string?)x.Attribute("Text"))?.Contains("VES", StringComparison.Ordinal) == true);

        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Pos.Desktop", "OpenCashWindow.xaml.cs"));
        Assert.Contains("public string Currency { get; private set; } = \"VES\";", source);
        Assert.DoesNotContain("CurrencyBox.SelectedItem", source);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}
