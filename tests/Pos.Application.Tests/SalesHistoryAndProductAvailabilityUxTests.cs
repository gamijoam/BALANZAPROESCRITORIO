using System.Xml.Linq;
using System.Net;
using System.Net.Http.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>Acceptance criteria for UX-002 and UX-003 approved for the POS workflows.</summary>
public sealed class SalesHistoryAndProductAvailabilityUxTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Sales_history_exposes_payment_operator_and_status_filters_and_sends_them_to_the_service()
    {
        var view = LoadView("SalesHistoryWindow.xaml");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "PaymentMethodFilterBox");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "OperatorFilterBox");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SaleStatusFilterBox");

        var source = LoadSource("src", "Pos.Desktop", "SalesHistoryWindow.xaml.cs");
        Assert.Contains("PaymentMethodFilterBox", source);
        Assert.Contains("OperatorFilterBox", source);
        Assert.Contains("SaleStatusFilterBox", source);
        Assert.Contains("GetSalesDetailReportAsync", source);

        var endpoint = LoadSource("src", "Pos.Server", "Hito4Endpoints.cs");
        Assert.Contains("paymentMethod", endpoint);
        Assert.Contains("operatorName", endpoint);
        Assert.Contains("status", endpoint);
    }

    [Fact]
    public void Management_reports_scope_filters_to_the_selected_report_and_search_sales_across_pages()
    {
        var view = LoadView("ManagementWindow.xaml");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SalesPaymentMethodBox");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SalesOperatorBox");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SalesStatusBox");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SalesDetailSearchBox" && x.Attribute("TextChanged") is null);
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "SalesDetailFiltersPanel");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "CashFiltersPanel");
        Assert.Contains(view.Descendants(), x => (string?)x.Attribute(Xaml + "Name") == "InventoryFiltersPanel");

        var window = LoadSource("src", "Pos.Desktop", "ManagementWindow.xaml.cs");
        Assert.Contains("SalesPaymentMethodBox", window);
        Assert.Contains("SalesOperatorBox", window);
        Assert.Contains("SalesStatusBox", window);
        Assert.Contains("query:", window);
        Assert.DoesNotContain("ApplySalesDetailFilter();", window);

        var api = LoadSource("src", "Pos.Desktop.Presentation", "PosApiClient.cs");
        Assert.Contains("&query=", api);
        var endpoint = LoadSource("src", "Pos.Server", "Hito4Endpoints.cs");
        Assert.Contains("string? query", endpoint);
        Assert.Contains("salesSearch", endpoint);
    }

    [Fact]
    public void Product_search_shows_stock_and_blocks_add_for_out_of_stock_items()
    {
        var view = LoadView("ProductSelectionWindow.xaml");
        Assert.Contains(view.Descendants(Presentation + "TextBlock"), x =>
            (string?)x.Attribute("Text") == "Existencia");
        Assert.Contains(view.Descendants(Presentation + "TextBlock"), x =>
            ((string?)x.Attribute("Text"))?.Contains("StockLabel", StringComparison.Ordinal) == true);

        var contracts = LoadSource("src", "Pos.Desktop.Presentation", "Contracts.cs");
        Assert.Contains("StockQuantity", contracts);
        var api = LoadSource("src", "Pos.Desktop.Presentation", "PosApiClient.cs");
        Assert.Contains("product.Quantity", api);
        var endpoint = LoadSource("src", "Pos.Server", "Program.cs");
        Assert.Contains("quantity =", endpoint);

        var selector = LoadSource("src", "Pos.Desktop", "ProductSelectionWindow.xaml.cs");
        Assert.Contains("StockQuantity is <= 0m", selector);
        var viewModel = LoadSource("src", "Pos.Desktop.Presentation", "PosViewModel.cs");
        Assert.Contains("StockQuantity is <= 0m", viewModel);
    }

    [Fact]
    public async Task Sales_history_sends_payment_operator_and_status_filters_in_the_request()
    {
        using var handler = new FilterRequestHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", Guid.NewGuid());

        await client.GetSalesDetailReportAsync(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1), "VES", 1,
            paymentMethod: "Pago móvil", operatorName: "ana", status: "Devuelta parcialmente", query: "harina");

        Assert.Contains("paymentMethod=Pago%20m%C3%B3vil", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operatorName=ana", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status=Devuelta%20parcialmente", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("query=harina", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Product_search_maps_server_stock_quantity_into_product_info()
    {
        using var handler = new FilterRequestHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", Guid.NewGuid());

        var product = Assert.Single(await client.SearchProductsAsync("SKU-1"));

        Assert.Equal(3m, product.StockQuantity);
    }

    private static XDocument LoadView(string name) => XDocument.Load(Path.Combine(Root(), "src", "Pos.Desktop", name));

    private static string LoadSource(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }

    private sealed class FilterRequestHandler : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(), expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token" })
                });

            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/api/v1/reports/sales/detail")
            {
                Query = request.RequestUri.Query;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { page = 1, pageSize = 250, totalCount = 0, sales = Array.Empty<object>() })
                });
            }
            Assert.Equal("/api/v1/products", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { id = Guid.NewGuid(), sku = "SKU-1", name = "Producto", unitPrice = 1m, currency = "USD", hasImage = false, quantity = 3m } })
            });
        }
    }
}
