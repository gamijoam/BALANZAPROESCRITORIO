using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>Acceptance criteria for approved report improvements UX-005 and UX-006.</summary>
public sealed class ReportDrilldownUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Product_sales_report_shows_sold_returned_and_net_units_and_amounts()
    {
        var view = LoadView("ManagementWindow.xaml");
        Assert.Contains(view.Descendants(Presentation + "TabItem"), x => (string?)x.Attribute("Header") == "Ventas por producto");
        Assert.Contains(view.Descendants(Presentation + "DataGridTextColumn"), x => (string?)x.Attribute("Header") == "N.º venta");
        foreach (var column in new[] { "Vendidas", "Devueltas", "Neto unidades", "Bruto", "Devuelto", "Neto" })
            Assert.Contains(view.Descendants(Presentation + "DataGridTextColumn"), x => (string?)x.Attribute("Header") == column);

        var endpoint = LoadSource("src", "Pos.Server", "Hito4Endpoints.cs");
        Assert.Contains("/api/v1/reports/products/sales", endpoint);
        Assert.Contains("SaleReturnLines", endpoint);
    }

    [Fact]
    public void Cash_report_exposes_payments_by_method_currency_and_cash_movements()
    {
        var contracts = LoadSource("src", "Pos.Desktop.Presentation", "Contracts.cs");
        Assert.Contains("CashMethodSummary", contracts);
        Assert.Contains("CashMovementSummary", contracts);
        Assert.Contains("Payments { get; init; }", contracts);
        Assert.Contains("Movements { get; init; }", contracts);

        var view = LoadView("ManagementWindow.xaml");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "CashSessionDetailsGrid");
        Assert.Contains(view.Descendants(Presentation + "DataGridTextColumn"), x => (string?)x.Attribute("Header") == "Método");
        Assert.Contains(view.Descendants(Presentation + "DataGridTextColumn"), x => (string?)x.Attribute("Header") == "Movimiento de caja");
    }

    [Fact]
    public async Task Product_sales_report_sends_period_and_currency_to_the_service()
    {
        using var handler = new ReportHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", Guid.NewGuid());

        var report = await client.GetProductSalesReportAsync(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1), "USD");

        Assert.Equal("/api/v1/reports/products/sales", handler.Path);
        Assert.Contains("currency=USD", handler.Query);
        Assert.Empty(report.Products);
    }

    [Fact]
    public async Task Cash_report_deserializes_payment_and_movement_details_for_the_selected_session()
    {
        using var handler = new ReportHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", Guid.NewGuid());

        var report = await client.GetCashSessionReportAsync(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1), "Todas");

        var session = Assert.Single(report.Sessions);
        Assert.Equal("Cerrada", session.StatusLabel);
        Assert.Equal("Efectivo", Assert.Single(session.Payments).Method);
        Assert.Equal("VES", Assert.Single(session.Movements).Currency);
    }

    private static XDocument LoadView(string name) => XDocument.Load(Path.Combine(Root(), "src", "Pos.Desktop", name));
    private static string LoadSource(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }

    private sealed class ReportHandler : HttpMessageHandler
    {
        public string Path { get; private set; } = string.Empty;
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(), expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token" })
                });

            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            Path = request.RequestUri.AbsolutePath;
            Query = request.RequestUri.Query;
            if (Path == "/api/v1/reports/cash-sessions")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        page = 1, pageSize = 250, totalCount = 1,
                        sessions = new[]
                        {
                            new
                            {
                                id = Guid.NewGuid(), status = "Closed", currency = "VES", openedAtUtc = DateTime.UtcNow.AddHours(-1), closedAtUtc = DateTime.UtcNow,
                                operatorName = "ana", openingUsd = 0m, openingVes = 0m, expectedUsd = 0m, countedUsd = 0m, varianceUsd = 0m,
                                expectedVes = 100m, countedVes = 100m, varianceVes = 0m,
                                payments = new[] { new { operation = "Cobro", method = "Efectivo", currency = "VES", amount = 100m } },
                                movements = new[] { new { direction = "In", currency = "VES", amount = 5m, reason = "Fondo", occurredAtUtc = DateTime.UtcNow } }
                            }
                        }
                    })
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { fromUtc = DateTime.UtcNow.Date, toUtc = DateTime.UtcNow.Date.AddDays(1), products = Array.Empty<object>() })
            });
        }
    }
}
