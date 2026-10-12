using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class CartMouseActionsUxTests
{
    [Fact]
    public void Cart_edits_quantity_inline_and_does_not_open_a_line_editor()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "Pos.Desktop", "MainWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var cart = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CartGrid");
        Assert.Equal("False", (string?)cart.Attribute("IsReadOnly"));
        var quantity = cart.Descendants().Single(element => element.Name.LocalName == "DataGridTextColumn" && (string?)element.Attribute("Header") == "Cantidad");
        Assert.Contains("Path=\"Quantity\"", quantity.ToString());
        Assert.DoesNotContain(cart.Descendants().Where(element => element.Name.LocalName == "Button"), element => ((string?)element.Attribute("Content"))?.Contains("Editar", StringComparison.OrdinalIgnoreCase) == true);

        var code = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("new CartLineEditWindow", code);
    }

    [Fact]
    public void Every_cart_row_has_a_direct_remove_button_without_prior_selection()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Pos.Desktop", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var cart = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "CartGrid");
        var handlers = cart.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Select(element => (string?)element.Attribute("Click"))
            .ToArray();

        Assert.Contains("RemoveCartLine_Click", handlers);
        Assert.DoesNotContain("EditLine_Click", handlers);
    }

    [Fact]
    public void Cart_keyboard_shortcuts_focus_and_navigate_editable_quantity_without_deleting_on_f8()
    {
        var root = FindRepositoryRoot();
        var xaml = XDocument.Load(Path.Combine(root, "src", "Pos.Desktop", "MainWindow.xaml"));
        var text = string.Join(" ", xaml.Descendants().Attributes().Select(attribute => attribute.Value));
        Assert.Contains("F8 Editar cantidad", text);
        Assert.Contains("Supr Quitar", text);

        var code = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "MainWindow.xaml.cs"));
        Assert.Contains("FocusCartQuantity", code);
        Assert.Contains("Key.Up", code);
        Assert.Contains("Key.Down", code);
        Assert.Contains("Key.Delete", code);
        var f8Handler = code[code.IndexOf("case Key.F8:", StringComparison.Ordinal)..];
        f8Handler = f8Handler[..f8Handler.IndexOf("case Key.Delete", StringComparison.Ordinal)];
        Assert.Contains("FocusCartQuantity", f8Handler);
        Assert.DoesNotContain("RemoveProduct", f8Handler);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0,5", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1,2345", false)]
    public void Inline_quantity_validation_accepts_only_positive_values_with_three_decimals(string value, bool expected)
    {
        var rule = new Pos.Desktop.CartQuantityValidationRule();
        Assert.Equal(expected, rule.Validate(value, Pos.Desktop.Presentation.PosNumberFormat.Culture).IsValid);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
