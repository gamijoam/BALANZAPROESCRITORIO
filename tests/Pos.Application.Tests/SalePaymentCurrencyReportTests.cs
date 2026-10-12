using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>
/// Acceptance criteria for the payment section of a sale report:
/// - Keep the sale's currency separate from tender currency.
/// - Show each tender in the currency and amount actually received.
/// - For mixed-currency tender, show the equivalent in the sale currency using the saved rate.
/// - If the saved rate is absent, do not invent an equivalent.
/// </summary>
public sealed class SalePaymentCurrencyReportTests
{
    private static readonly System.Xml.Linq.XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly System.Xml.Linq.XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Usd_payment_for_ves_sale_shows_actual_amount_and_historical_ves_equivalent()
    {
        var payment = new SaleDetailPayment("Efectivo", 2m, "USD", 50m);

        Assert.Equal("Efectivo: USD 2,00 (equivale a VES 100,00; tasa 50,00)",
            payment.DescribeForSaleCurrency("VES"));
    }

    [Fact]
    public void Mixed_currency_payments_are_described_independently_in_sale_currency()
    {
        var payments = new[]
        {
            new SaleDetailPayment("Efectivo", 2m, "USD", 50m),
            new SaleDetailPayment("Transferencia", 40m, "VES", null)
        };

        Assert.Equal("Efectivo: USD 2,00 (equivale a VES 100,00; tasa 50,00)", payments[0].DescribeForSaleCurrency("VES"));
        Assert.Equal("Transferencia: VES 40,00", payments[1].DescribeForSaleCurrency("VES"));
    }

    [Fact]
    public void Missing_historical_rate_is_explicitly_reported_without_calculating_a_value()
    {
        var payment = new SaleDetailPayment("Efectivo", 2m, "USD", null);

        Assert.Equal("Efectivo: USD 2,00 (equivalencia a VES no disponible)", payment.DescribeForSaleCurrency("VES"));
    }

    [Fact]
    public void Sale_detail_exposes_auditable_rounding_adjustment()
    {
        var sale = new SaleDetailRow(Guid.NewGuid(), DateTime.UtcNow, 1_000m, "VES", "demo", "Venta general", Guid.NewGuid(), [], [])
        {
            RoundingAdjustment = -0.10m
        };

        Assert.Equal("Ajuste de redondeo: −0,10 VES", sale.RoundingAdjustmentLabel);
    }

    [Fact]
    public void Sales_detail_labels_the_sale_currency_separately_from_received_payments()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Pos.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var view = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "src", "Pos.Desktop", "ManagementWindow.xaml"));
        var grid = Assert.Single(view.Descendants(Presentation + "DataGrid"), x => (string?)x.Attribute(Xaml + "Name") == "SalesDetailGrid");
        Assert.Contains(grid.Descendants(Presentation + "DataGridTextColumn"), x => (string?)x.Attribute("Header") == "Moneda de venta");
        Assert.Contains(view.Descendants(Presentation + "TextBlock"), x => (string?)x.Attribute(Xaml + "Name") == "SaleDetailTitle" &&
            ((string?)x.Attribute("TextWrapping")) == "Wrap");
    }

}
