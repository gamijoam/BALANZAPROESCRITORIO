using System.Xml.Linq;

namespace Pos.Application.Tests;

/// <summary>
/// Criterio UX-001: antes de cerrar, el cajero compara el efectivo contado y esperado
/// por moneda y ve la diferencia actualizada mientras escribe.
/// </summary>
public sealed class CashClosePreviewTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Cash_close_view_shows_expected_counted_and_live_difference_for_both_currencies()
    {
        var view = XDocument.Load(Path.Combine(FindRoot().FullName, "src", "Pos.Desktop", "CloseCashWindow.xaml"));
        var names = view.Descendants().Attributes(Xaml + "Name").Select(x => x.Value).ToArray();

        Assert.Contains("ExpectedUsdLabel", names);
        Assert.Contains("ExpectedVesLabel", names);
        Assert.Contains("DifferenceUsdLabel", names);
        Assert.Contains("DifferenceVesLabel", names);
        Assert.DoesNotContain(view.Descendants(Presentation + "TextBox"), x => x.Attribute("TextChanged") is not null);

        var code = File.ReadAllText(Path.Combine(FindRoot().FullName, "src", "Pos.Desktop", "CloseCashWindow.xaml.cs"));
        Assert.Contains("CountedUsdBox.TextChanged += CountedAmount_TextChanged", code);
        Assert.Contains("CountedVesBox.TextChanged += CountedAmount_TextChanged", code);
        Assert.Contains("var difference = counted - expected.Value", code);
        Assert.Contains("UpdateDifference(CountedUsdBox.Text, _expectedUsd, DifferenceUsdLabel", code);
        Assert.Contains("UpdateDifference(CountedVesBox.Text, _expectedVes, DifferenceVesLabel", code);
        var mainWindow = File.ReadAllText(Path.Combine(FindRoot().FullName, "src", "Pos.Desktop", "MainWindow.xaml.cs"));
        Assert.Contains("RefreshCashSessionAsync", mainWindow);
    }

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}
