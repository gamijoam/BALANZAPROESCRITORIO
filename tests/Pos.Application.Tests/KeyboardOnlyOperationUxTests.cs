using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class KeyboardOnlyOperationUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Payment_dialog_exposes_shortcuts_focus_and_standard_enter_escape_actions()
    {
        var document = LoadXaml("PaymentWindow.xaml");
        var window = document.Root!;
        var buttons = document.Descendants(Presentation + "Button").ToArray();

        Assert.Equal("Window_PreviewKeyDown", (string?)window.Attribute("PreviewKeyDown"));
        Assert.Equal("Window_Loaded", (string?)window.Attribute("Loaded"));
        Assert.Contains(buttons, x => (string?)x.Attribute(Xaml + "Name") == "ConfirmButton" && (string?)x.Attribute("IsDefault") == "True");
        Assert.Contains(buttons, x => (string?)x.Attribute("Content") == "Cancelar" && (string?)x.Attribute("IsCancel") == "True");
        var methods = buttons.Where(x => ((string?)x.Attribute("Tag")) is "Efectivo" or "Punto de venta" or "Transferencia" or "Pago móvil" or "Crédito").ToArray();
        Assert.Equal(5, methods.Length);
        Assert.Equal(new[] { "Alt+1", "Alt+2", "Alt+3", "Alt+4", "Alt+5" },
            methods.Select(x => ((string?)x.Attribute("ToolTip"))?.Split(':')[0]).ToArray());
        var mobilePayment = Assert.Single(methods, x => (string?)x.Attribute("Tag") == "Pago móvil");
        Assert.StartsWith("Alt+4:", (string?)mobilePayment.Attribute("ToolTip"));
        Assert.Contains(mobilePayment.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute("Text") == "Pago móvil");
        Assert.Contains(mobilePayment.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute("Text") == "Alt+4");

        var code = LoadCode("PaymentWindow.xaml.cs");
        Assert.Contains("modifiers != ModifierKeys.Control && modifiers != ModifierKeys.Alt", code);
        Assert.Contains("ModifierKeys.Alt", code);
        Assert.Contains("e.Key == Key.System ? e.SystemKey : e.Key", code);
        Assert.Contains("PaymentMethod_Click", code);
        Assert.Contains("CashMethodButton.Focus()", code);
        Assert.Contains("pressedKey == Key.Enter", code);
        Assert.Contains("PaymentsGrid.IsKeyboardFocusWithin", code);
        Assert.Contains("PaymentsGrid.CommitEdit(DataGridEditingUnit.Cell, true)", code);
        Assert.Contains("if (ConfirmButton.IsEnabled)", code);
        Assert.Contains("Confirm_Click(ConfirmButton", code);
    }

    [Fact]
    public void Customer_search_enter_selects_and_focuses_first_result_and_enter_on_grid_uses_it()
    {
        var document = LoadXaml("CustomersWindow.xaml");
        Assert.Contains(document.Descendants(Presentation + "DataGrid"), x =>
            (string?)x.Attribute(Xaml + "Name") == "CustomersGrid" &&
            (string?)x.Attribute("PreviewKeyDown") == "CustomersGrid_PreviewKeyDown");

        var code = LoadCode("CustomersWindow.xaml.cs");
        Assert.Contains("SearchBox.Focus()", code);
        Assert.Contains("CustomersGrid.SelectedIndex = 0", code);
        Assert.Contains("CustomersGrid.Focus()", code);
        Assert.Contains("Key.Up or Key.Down", code);
        Assert.Contains("Select_Click", code);
    }

    [Fact]
    public void Main_sections_have_access_keys_and_modal_actions_restore_scanner_focus()
    {
        var document = LoadXaml("MainWindow.xaml");
        var sectionButtons = document.Descendants(Presentation + "Button")
            .Where(x => ((string?)x.Attribute("Content"))?.Contains("_") == true)
            .ToArray();

        Assert.Contains(sectionButtons, x => ((string?)x.Attribute("Content"))?.Contains("_Historial", StringComparison.Ordinal) == true);
        Assert.Contains(sectionButtons, x => ((string?)x.Attribute("Content"))?.Contains("_Inventario", StringComparison.Ordinal) == true);
        Assert.Contains(sectionButtons, x => ((string?)x.Attribute("Content"))?.Contains("C_lientes", StringComparison.Ordinal) == true);

        var code = LoadCode("MainWindow.xaml.cs");
        Assert.Contains("private void RefocusScanner()", code);
        Assert.Contains("RefocusScanner();", code);
    }

    private static XDocument LoadXaml(string fileName) => XDocument.Load(PathFor("src", "Pos.Desktop", fileName));
    private static string LoadCode(string fileName) => File.ReadAllText(PathFor("src", "Pos.Desktop", fileName));

    private static string PathFor(params string[] paths)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. paths]);
    }
}
