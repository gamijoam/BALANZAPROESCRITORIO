using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class DisabledControlsAndCustomersLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Payment_methods_have_explicit_disabled_and_keyboard_focus_visual_states()
    {
        var style = Assert.Single(LoadView("PaymentWindow.xaml").Descendants(Presentation + "Style"),
            x => (string?)x.Attribute(Xaml + "Key") == "PaymentMethodButton");
        var triggers = style.Descendants(Presentation + "Trigger").ToArray();

        Assert.Contains(triggers, x => (string?)x.Attribute("Property") == "IsEnabled" && (string?)x.Attribute("Value") == "False");
        Assert.Contains(triggers, x => (string?)x.Attribute("Property") == "IsKeyboardFocused" && (string?)x.Attribute("Value") == "True");
    }

    [Fact]
    public void Sidebar_buttons_keep_a_defined_disabled_and_keyboard_focus_appearance()
    {
        var style = Assert.Single(LoadView("MainWindow.xaml").Descendants(Presentation + "Style"),
            x => (string?)x.Attribute(Xaml + "Key") == "ToolButton");
        var triggers = style.Descendants(Presentation + "Trigger").ToArray();

        Assert.Contains(triggers, x => (string?)x.Attribute("Property") == "IsEnabled" && (string?)x.Attribute("Value") == "False");
        Assert.Contains(triggers, x => (string?)x.Attribute("Property") == "IsKeyboardFocused" && (string?)x.Attribute("Value") == "True");
    }

    [Fact]
    public void Customer_status_is_bounded_and_actions_use_a_responsive_two_row_layout()
    {
        var document = LoadView("CustomersWindow.xaml");
        var status = Assert.Single(document.Descendants(Presentation + "TextBlock"),
            x => (string?)x.Attribute(Xaml + "Name") == "StatusLabel");
        var actions = Assert.Single(document.Descendants(Presentation + "UniformGrid"),
            x => (string?)x.Attribute("Grid.Row") == "1" && (string?)x.Attribute("Columns") == "3");

        Assert.Equal("NoWrap", (string?)status.Attribute("TextWrapping"));
        Assert.Equal("CharacterEllipsis", (string?)status.Attribute("TextTrimming"));
        Assert.Contains("Binding Text", (string?)status.Attribute("ToolTip"));
        Assert.Equal(6, actions.Descendants(Presentation + "Button").Count());
        Assert.DoesNotContain(document.Descendants(Presentation + "DockPanel"), x => (string?)x.Attribute("Grid.Row") == "3");
    }

    [Fact]
    public void Customer_header_keeps_search_shortcuts_out_of_the_search_layout()
    {
        var document = LoadView("CustomersWindow.xaml");
        var subtitle = Assert.Single(document.Descendants(Presentation + "TextBlock"),
            x => ((string?)x.Attribute("Text"))?.StartsWith("Cuentas en USD", StringComparison.Ordinal) == true);

        Assert.DoesNotContain("F2", (string?)subtitle.Attribute("Text"));
        Assert.DoesNotContain("Enter busca", (string?)subtitle.Attribute("Text"));
        Assert.Contains(document.Descendants(Presentation + "TextBox"), x =>
            ((string?)x.Attribute("ToolTip"))?.Contains("F2", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void F2_customer_search_ignores_modified_function_keys()
    {
        var customerCode = LoadSource("CustomersWindow.xaml.cs");
        var mainCode = LoadSource("MainWindow.xaml.cs");

        Assert.Contains("Keyboard.Modifiers != ModifierKeys.None", customerCode);
        Assert.Contains("Keyboard.Modifiers != ModifierKeys.None", mainCode);
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
        Assert.NotNull(directory);
        return directory!;
    }
}
