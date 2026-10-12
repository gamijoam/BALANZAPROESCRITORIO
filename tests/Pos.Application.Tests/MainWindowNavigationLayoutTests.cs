using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class MainWindowNavigationLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Primary_navigation_has_ten_visible_actions_without_a_scroll_viewer()
    {
        var document = LoadView();
        var navigation = Assert.Single(document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute("Grid.Column") == "2" && (string?)element.Attribute("Grid.Row") == "1");

        Assert.Empty(navigation.Descendants(Presentation + "ScrollViewer"));
        var namedButtons = navigation.Descendants(Presentation + "Button")
            .Where(button => !button.Ancestors(Presentation + "ContextMenu").Any())
            .Select(button => (string?)button.Attribute("AutomationProperties.AutomationId"))
            .Where(id => id?.StartsWith("Navigate", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(10, namedButtons.Length);
        Assert.Contains("NavigateModules", namedButtons);
        Assert.Contains(navigation.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute(Xaml + "Name") == "ModulesButton" &&
            (string?)button.Attribute("Click") == "ModulesButton_Click");
    }

    [Fact]
    public void Module_contributions_and_management_action_are_hosted_inside_the_modules_popup()
    {
        var document = LoadView();
        var popup = Assert.Single(document.Descendants(Presentation + "ContextMenu"),
            element => (string?)element.Attribute(Xaml + "Name") == "ModulesPopup");

        Assert.Contains(popup.Descendants(Presentation + "MenuItem"), item =>
            (string?)item.Attribute(Xaml + "Name") == "ModuleMenuEmpty" &&
            (string?)item.Attribute("IsEnabled") == "False");
        Assert.Contains(popup.Descendants(Presentation + "MenuItem"), item =>
            (string?)item.Attribute(Xaml + "Name") == "AdminModulesMenuItem" &&
            (string?)item.Attribute("Visibility") == "Collapsed");

        var code = LoadSource();
        Assert.Contains("ModulesPopup.IsOpen = !ModulesPopup.IsOpen", code);
        Assert.Contains("ModulesPopup_Opened", code);
        Assert.Contains("ModulesPopup_PreviewKeyDown", code);
        Assert.Contains("if (e.Key != Key.Escape) return", code);
        Assert.Contains("ModulesButton.Focus()", code);
        Assert.Contains("AdminModulesMenuItem.Visibility", code);
        Assert.Contains("GrantedPermissions.Contains(menu.RequiredPermission)", code);
        Assert.Contains("ModulesPopup.Items.Insert", code);
    }

    [Fact]
    public void Server_reports_module_management_visibility_without_consuming_a_pin_approval()
    {
        var program = File.ReadAllText(PathFor("src", "Pos.Server", "Program.cs"));

        Assert.Contains("/api/v1/modules/management-access", program);
        Assert.Contains("Permission.ManageModules", program);
        Assert.Contains("mode != PermissionAccessMode.Deny", program);
        Assert.Contains("mode == PermissionAccessMode.RequireAdminPin", program);
    }

    [Fact]
    public void Header_keeps_metrics_after_the_brand_in_a_left_shifted_compact_grid()
    {
        var document = LoadView();
        var header = Assert.Single(document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute("Grid.Row") == "0" &&
                       element.Descendants(Presentation + "TextBlock").Any(text => (string?)text.Attribute("Text") == "PUNTO DE VENTA"));
        var columns = header.Descendants(Presentation + "Grid.ColumnDefinitions").First().Elements(Presentation + "ColumnDefinition").ToArray();
        Assert.Equal(4, columns.Length);
        Assert.Equal("1.7*", (string?)columns[0].Attribute("Width"));
        Assert.Contains(header.Descendants(Presentation + "Border"), element => (string?)element.Attribute("Grid.Column") == "1" && (string?)element.Attribute("Background") == "#54B85D");
        Assert.Contains(header.Descendants(Presentation + "Border"), element => (string?)element.Attribute("Grid.Column") == "2" && (string?)element.Attribute("Background") == "#E9AD27");
        Assert.Contains(header.Descendants(Presentation + "Border"), element => (string?)element.Attribute("Grid.Column") == "3" && (string?)element.Attribute("Background") == "#E74D54");
    }

    private static XDocument LoadView() => XDocument.Load(PathFor("src", "Pos.Desktop", "MainWindow.xaml"));
    private static string LoadSource() => File.ReadAllText(PathFor("src", "Pos.Desktop", "MainWindow.xaml.cs"));

    private static string PathFor(params string[] paths)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. paths]);
    }
}
