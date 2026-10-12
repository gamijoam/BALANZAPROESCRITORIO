using System.Xml.Linq;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class SalesHistoryUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Sales_history_uses_a_full_width_list_and_compact_selected_sale_details()
    {
        var view = LoadView();

        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SalesListPanel");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SaleDetailsPanel");
        Assert.Contains(view.Descendants(Presentation + "Border"), x =>
            (string?)x.Attribute(Xaml + "Name") == "PaymentDetailsPanel");
        Assert.Contains(view.Descendants(Presentation + "TextBlock"), x =>
            (string?)x.Attribute("Text") == "FORMAS DE PAGO");
        Assert.Contains(view.Descendants(Presentation + "TextBlock"), x =>
            (string?)x.Attribute(Xaml + "Name") == "ReceiptPayments");
        Assert.DoesNotContain(view.Descendants(Presentation + "Expander"), x =>
            (string?)x.Attribute(Xaml + "Name") == "PaymentsExpander");
        Assert.DoesNotContain(view.Descendants(), x =>
            (string?)x.Attribute("Header") == "Ver detalle de pagos por método");
        Assert.DoesNotContain(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "ReceiptPreviewPanel");
        Assert.Contains(view.Descendants(Presentation + "DataGrid"), x =>
            (string?)x.Attribute(Xaml + "Name") == "SalesGrid");
        Assert.Contains(view.Descendants(Presentation + "DatePicker"), x =>
            (string?)x.Attribute(Xaml + "Name") == "FromDatePicker");
        Assert.Contains(view.Descendants(Presentation + "DatePicker"), x =>
            (string?)x.Attribute(Xaml + "Name") == "ToDatePicker");
        Assert.Contains(view.Descendants(Presentation + "TextBox"), x =>
            (string?)x.Attribute(Xaml + "Name") == "SearchBox");
        Assert.Contains(view.Descendants(Presentation + "Button"), x =>
            (string?)x.Attribute("Content") == "Reimprimir comprobante");
        Assert.Contains(view.Descendants(Presentation + "Button"), x =>
            (string?)x.Attribute("Content") == "Registrar devolución");
        Assert.Contains(view.Descendants(), x =>
            (string?)x.Attribute("Header") == "Total y referencia");
        Assert.Contains(view.Descendants(), x =>
            (string?)x.Attribute("Header") == "Monedas recibidas");
        Assert.DoesNotContain(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") is "ReceiptCustomer" or "ReceiptStatus");
        Assert.DoesNotContain(view.Descendants(Presentation + "Button"), x =>
            string.Equals((string?)x.Attribute("Content"), "Modificar venta", StringComparison.OrdinalIgnoreCase));

        var main = XDocument.Load(Path.Combine(FindRepositoryRoot().FullName, "src", "Pos.Desktop", "MainWindow.xaml"));
        Assert.Contains(main.Descendants(Presentation + "Button"), x =>
            (string?)x.Attribute("AutomationProperties.Name") == "Historial de ventas" && (string?)x.Attribute("Click") == "SalesHistoryTool_Click");
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "src", "Pos.Desktop", "SalesHistoryWindow.xaml.cs"));
        Assert.Contains("SaleHistoryReceiptMapper.ToReceipt(sale)", source);
        Assert.Contains("new SaleReturnWindow(_viewModel, sale)", source);
    }

    [Fact]
    public void Sales_history_receipt_preserves_the_original_sale_lines_payments_and_rounding()
    {
        var saleId = Guid.NewGuid();
        var issuedAt = new DateTime(2026, 10, 10, 19, 5, 0, DateTimeKind.Utc);
        var sale = new SaleDetailRow(saleId, issuedAt, 9000m, "VES", "demo", "Gabriel", Guid.NewGuid(),
            [new SaleDetailLine("SKU-1", "Producto de prueba", 2m, 4500m, 9000m, "USD", 5m)],
            [new SaleDetailPayment("Efectivo", 3500m, "VES", null), new SaleDetailPayment("Pago móvil", 6.11m, "USD", 900m)])
        { RoundingAdjustment = 1m };

        var receipt = SaleHistoryReceiptMapper.ToReceipt(sale);

        Assert.Equal(saleId, receipt.SaleId);
        Assert.Equal(issuedAt, receipt.IssuedAtUtc);
        Assert.Equal("VES", receipt.Currency);
        Assert.Equal("Gabriel", receipt.CustomerName);
        Assert.Equal(9000m, receipt.Total);
        Assert.Equal(1m, receipt.RoundingAdjustment);
        Assert.Equal("SKU-1", Assert.Single(receipt.Lines).Sku);
        Assert.Equal("Producto de prueba", receipt.Lines[0].Description);
        Assert.Equal(2m, receipt.Lines[0].Quantity);
        Assert.Equal(4500m, receipt.Lines[0].UnitPrice);
        Assert.Equal(9000m, receipt.Lines[0].Total);
        Assert.Equal("USD", receipt.Lines[0].OriginalCurrency);
        Assert.Equal(5m, receipt.Lines[0].OriginalUnitPrice);
        Assert.Null(receipt.Lines[0].AppliedExchangeRate);
        Assert.Equal(2, receipt.Payments.Count);
        Assert.Equal("Efectivo", receipt.Payments[0].Method);
        Assert.Equal(3500m, receipt.Payments[0].Amount);
        Assert.Equal("VES", receipt.Payments[0].Currency);
        Assert.Equal("Pago móvil", receipt.Payments[1].Method);
        Assert.Equal(6.11m, receipt.Payments[1].Amount);
        Assert.Equal("USD", receipt.Payments[1].Currency);
    }

    [Fact]
    public void Sales_history_receipt_mapper_rejects_a_missing_sale()
        => Assert.Throws<ArgumentNullException>(() => SaleHistoryReceiptMapper.ToReceipt(null!));

    [Fact]
    public void Sales_history_shows_historical_usd_reference_and_each_payment_currency()
    {
        var sale = new SaleDetailRow(Guid.NewGuid(), DateTime.UtcNow, 9000m, "VES", "demo", "Gabriel", Guid.NewGuid(),
            [], [new SaleDetailPayment("Efectivo", 3500m, "VES", null), new SaleDetailPayment("Transferencia", 6.11m, "USD", 900m)])
        { AppliedUsdToVesRate = 900m };

        Assert.Equal("Ref. USD 10,00", sale.HistoricalReferenceLabel);
        Assert.Equal("VES 3.500,00 + USD 6,11", sale.PaymentCurrenciesLabel);
    }

    [Fact]
    public void Sales_history_does_not_recalculate_historical_reference_when_current_rate_changes()
    {
        var sale = new SaleDetailRow(Guid.NewGuid(), DateTime.UtcNow, 9000m, "VES", "demo", "Gabriel", Guid.NewGuid(), [], [])
        { AppliedUsdToVesRate = 900m };

        Assert.Equal("Ref. USD 10,00", sale.HistoricalReferenceLabel);
    }

    [Fact]
    public void Sales_without_a_saved_historical_rate_do_not_use_a_fabricated_reference()
    {
        var sale = new SaleDetailRow(Guid.NewGuid(), DateTime.UtcNow, 9000m, "VES", "demo", "Gabriel", Guid.NewGuid(), [], []);

        Assert.Equal("Ref. USD no disponible", sale.HistoricalReferenceLabel);
    }

    private static XDocument LoadView()
    {
        var path = Path.Combine(FindRepositoryRoot().FullName, "src", "Pos.Desktop", "SalesHistoryWindow.xaml");
        Assert.True(File.Exists(path), "El historial de ventas debe tener una vista WPF propia.");
        return XDocument.Load(path);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}
