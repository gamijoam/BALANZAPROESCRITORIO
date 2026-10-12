using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class PaymentWindowLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Payment_guidance_and_actions_have_separate_bounded_footer_columns()
    {
        var document = LoadPaymentView();
        var error = Assert.Single(document.Descendants(Presentation + "TextBlock"),
            x => (string?)x.Attribute(Xaml + "Name") == "ErrorLabel");
        var footer = Assert.Single(document.Descendants(Presentation + "Grid"),
            x => (string?)x.Attribute("Grid.Row") == "3" &&
                 x.Elements().Any(child => child.Name.LocalName == "Grid.ColumnDefinitions"));
        var actions = Assert.Single(document.Descendants(Presentation + "StackPanel"),
            x => (string?)x.Attribute("Grid.Column") == "1"
                && x.Descendants(Presentation + "Button").Any(b => (string?)b.Attribute(Xaml + "Name") == "ConfirmButton"));

        Assert.Equal("3", (string?)footer.Attribute("Grid.Row"));
        Assert.Contains(footer.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute(Xaml + "Name") == "ErrorLabel");
        Assert.Equal("0", (string?)error.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)actions.Attribute("Grid.Column"));
        Assert.True(double.Parse((string?)error.Attribute("MaxHeight") ?? "0", System.Globalization.CultureInfo.InvariantCulture) <= 42);
        Assert.Equal("Wrap", (string?)error.Attribute("TextWrapping"));
        Assert.Contains(actions.Descendants(Presentation + "Button"), x => (string?)x.Attribute(Xaml + "Name") == "ConfirmButton");
    }

    [Fact]
    public void Payment_methods_are_visible_as_choices_before_any_payment_is_entered()
    {
        var document = LoadPaymentView();
        var panel = Assert.Single(document.Descendants(Presentation + "StackPanel"),
            x => (string?)x.Attribute(Xaml + "Name") == "PaymentMethodsPanel");
        var choices = panel.Descendants(Presentation + "Button").Select(x => (string?)x.Attribute("Tag")).ToArray();

        Assert.Equal(new string?[] { "Efectivo", "Punto de venta", "Transferencia", "Pago móvil", "Crédito" }, choices);
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), x =>
            ((string?)x.Attribute("Text"))?.Contains("Selecciona un método", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(document.Descendants(Presentation + "Button"), x =>
            ((string?)x.Attribute("Content"))?.Contains("teclado", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Payment_window_emphasizes_only_the_remaining_balance_in_both_currencies()
    {
        var document = LoadPaymentView();

        Assert.Contains(document.Descendants(Presentation + "DataGridTextColumn"), x =>
            (string?)x.Attribute("Header") == "Equiv. USD" &&
            ((string?)x.Attribute("Binding"))?.Contains("EquivalentUsdText", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), x =>
            (string?)x.Attribute(Xaml + "Name") == "OtherCurrencyDueLabel");
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), x =>
            ((string?)x.Attribute("Text"))?.Contains("PENDIENTE POR AGREGAR", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBlock"), x =>
            ((string?)x.Attribute("Text")) == "Pagado" || ((string?)x.Attribute("Text")) == "VENTA EN");
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBlock"), x =>
            ((string?)x.Attribute("Text")) == "Ref. USD");
    }

    [Fact]
    public void Payment_window_starts_without_assuming_cash_and_adds_pending_amount_for_selected_method()
    {
        var code = LoadPaymentSource();
        Assert.DoesNotContain("else _rows.Add(new PaymentRow(\"Efectivo\"", code);
        Assert.Contains("private void PaymentMethod_Click", code);
        Assert.Contains("(sender as FrameworkElement)?.Tag?.ToString()", code);
        Assert.Contains("new PaymentRow(method, PosNumberFormat.FormatAmount(pending)", code);
        Assert.Contains("PaymentsGrid.BeginEdit()", code);
        Assert.Contains("PaymentsGrid.SelectedItem", code);
    }

    private static XDocument LoadPaymentView()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "PaymentWindow.xaml"));
    }

    private static string LoadPaymentSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Pos.Desktop", "PaymentWindow.xaml.cs"));
    }
}
