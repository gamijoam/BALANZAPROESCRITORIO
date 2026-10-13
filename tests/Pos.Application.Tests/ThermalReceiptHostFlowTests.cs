namespace Pos.Application.Tests;

public sealed class ThermalReceiptHostFlowTests
{
    [Fact]
    public void Desktop_host_dispatches_confirmed_sales_to_the_optional_receipt_extension()
    {
        var root = FindRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "MainWindow.xaml.cs"));
        var abstraction = File.ReadAllText(Path.Combine(root, "src", "Modules", "Pos.Desktop.Modules.Abstractions", "IPosDesktopModule.cs"));

        Assert.Contains("IPosReceiptExtension", abstraction, StringComparison.Ordinal);
        Assert.Contains("RegisterReceiptExtension", abstraction, StringComparison.Ordinal);
        Assert.Contains("PrintAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CompleteSaleAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CreateReceiptPreviewWindow", mainWindow, StringComparison.Ordinal);
        Assert.Contains("La venta quedó confirmada, pero falló la impresión", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ThermalReceipts.Print", File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Sales_history_uses_the_same_optional_preview_for_reprints()
    {
        var root = FindRoot();
        var history = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "SalesHistoryWindow.xaml.cs"));
        Assert.Contains("receiptPreviewFactory", history, StringComparison.Ordinal);
        Assert.Contains("SaleHistoryReceiptMapper.ToReceipt", history, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
