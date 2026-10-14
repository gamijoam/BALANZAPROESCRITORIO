using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientInventoryTests
{
    [Fact]
    public async Task Product_search_maps_price_tiers_and_pending_or_classified_tax_state()
    {
        var id = Guid.NewGuid();
        using var handler = new InventoryHandler((request, _) =>
        {
            Assert.Equal("/api/v1/products", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { id, sku = "PAN-1", name = "Harina", unitPrice = 10m, price2 = 8m, price3 = 5m,
                    taxTreatment = "Taxable8", currency = "VES", hasImage = false, quantity = 4m } })
            });
        });
        using var client = await CreateClientAsync(handler);

        var product = Assert.Single(await client.SearchProductsAsync("PAN"));

        Assert.Equal(8m, product.Price2);
        Assert.Equal(5m, product.Price3);
        Assert.Equal("Taxable8", product.TaxTreatment);
        Assert.True(product.IsTaxClassified);
    }

    [Fact]
    public async Task Product_create_sends_all_tax_and_price_tier_values()
    {
        var id = Guid.NewGuid();
        using var handler = new InventoryHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/products", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(10m, body.RootElement.GetProperty("unitPrice").GetDecimal());
            Assert.Equal(8m, body.RootElement.GetProperty("price2").GetDecimal());
            Assert.Equal(5m, body.RootElement.GetProperty("price3").GetDecimal());
            Assert.Equal("Exempt", body.RootElement.GetProperty("taxTreatment").GetString());
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { id, sku = "PAN-1", name = "Harina", unitPrice = 10m, price2 = 8m, price3 = 5m,
                    taxTreatment = "Exempt", currency = "VES", initialStock = 4m, hasImage = false })
            };
        });
        using var client = await CreateClientAsync(handler);

        var result = await client.CreateProductAsync("PAN-1", "Harina", 10m, 8m, 5m, "Exempt", "VES", 4m);

        Assert.Equal(8m, result.Price2);
        Assert.Equal("Exempt", result.TaxTreatment);
    }

    [Fact]
    public async Task Product_tax_and_selected_price_tier_use_their_service_endpoints()
    {
        var id = Guid.NewGuid();
        using var handler = new InventoryHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath.EndsWith("/tax-treatment", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Equal("Taxable0", body.RootElement.GetProperty("taxTreatment").GetString());
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Price3", body.RootElement.GetProperty("priceTier").GetString());
                Assert.Equal(5m, body.RootElement.GetProperty("newPrice").GetDecimal());
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = await CreateClientAsync(handler);

        await client.SetProductTaxTreatmentAsync(id, "Taxable0");
        await client.ChangePriceAsync(id, "Price3", 5m, "Precio mayorista");
    }

    [Fact]
    public async Task Inventory_query_sends_filters_and_maps_active_state()
    {
        var id = Guid.NewGuid();
        using var handler = new InventoryHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/inventory", request.RequestUri!.AbsolutePath);
            Assert.Contains("query=harina%20pan", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("status=inactive", request.RequestUri.Query);
            Assert.Contains("currency=VES", request.RequestUri.Query);
            Assert.Contains("stock=available", request.RequestUri.Query);
            Assert.Contains("hasImage=true", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { id, sku = "PAN-1", name = "Harina Pan", unitPrice = 10m, currency = "VES", quantity = 4m, hasImage = true, active = false } })
            });
        });
        using var client = await CreateClientAsync(handler);

        var items = await client.GetInventoryAsync("harina pan", "inactive", "VES", "available", true);

        var item = Assert.Single(items);
        Assert.Equal(id, item.Id);
        Assert.False(item.Active);
    }

    [Fact]
    public async Task Warehouse_inventory_request_can_include_inactive_products()
    {
        var warehouseId = Guid.NewGuid();
        using var handler = new InventoryHandler((request, _) =>
        {
            Assert.Equal($"/api/v1/modules/warehouses/warehouses/{warehouseId}/stock", request.RequestUri!.AbsolutePath);
            Assert.Equal("?includeInactive=true", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        });
        using var client = await CreateClientAsync(handler);

        await client.GetWarehouseStockAsync(warehouseId, includeInactive: true);
    }

    [Fact]
    public async Task Product_edit_uses_authenticated_patch_and_sends_new_sku_and_name()
    {
        var id = Guid.NewGuid();
        using var handler = new InventoryHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal($"/api/v1/products/{id}", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("PAN-2", body.RootElement.GetProperty("sku").GetString());
            Assert.Equal("Harina Pan Extra", body.RootElement.GetProperty("name").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = await CreateClientAsync(handler);

        await client.UpdateProductAsync(id, "PAN-2", "Harina Pan Extra");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Product_active_state_uses_authenticated_patch(bool active)
    {
        var id = Guid.NewGuid();
        using var handler = new InventoryHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal($"/api/v1/products/{id}/active", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(active, body.RootElement.GetProperty("active").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = await CreateClientAsync(handler);

        await client.SetProductActiveAsync(id, active);
    }

    private static async Task<PosApiClient> CreateClientAsync(InventoryHandler handler)
    {
        var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler, disposeHandler: false));
        await client.LoginAsync("demo", "1234", Guid.NewGuid());
        return client;
    }

    private sealed class InventoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(), expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "inventory-test-token" })
                });
            Assert.Equal("inventory-test-token", request.Headers.Authorization?.Parameter);
            return send(request, cancellationToken);
        }
    }
}
