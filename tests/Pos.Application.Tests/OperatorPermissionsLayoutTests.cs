using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class OperatorPermissionsLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Permission_editor_uses_row_selection_so_loading_the_matrix_does_not_crash_wpf()
    {
        var grid = Assert.Single(LoadPermissionEditor().Descendants(Presentation + "DataGrid"),
            x => (string?)x.Attribute(Xaml + "Name") == "PermissionGrid");

        Assert.Equal("FullRow", (string?)grid.Attribute("SelectionUnit"));
        Assert.Equal("Single", (string?)grid.Attribute("SelectionMode"));
    }

    [Fact]
    public void Cart_grid_uses_row_selection_when_binding_the_selected_item()
    {
        var document = LoadView("MainWindow.xaml");
        var grid = Assert.Single(document.Descendants(Presentation + "DataGrid"),
            x => (string?)x.Attribute(Xaml + "Name") == "CartGrid");

        Assert.Equal("FullRow", (string?)grid.Attribute("SelectionUnit"));
        Assert.Equal("Single", (string?)grid.Attribute("SelectionMode"));
    }

    private static XDocument LoadPermissionEditor() => LoadView("OperatorPermissionsWindow.xaml");

    private static XDocument LoadView(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", name));
    }
}
