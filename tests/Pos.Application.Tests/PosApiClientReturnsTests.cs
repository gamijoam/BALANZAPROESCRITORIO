using System.Net;
using System.Text;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientReturnsTests
{
    [Fact]
    public async Task Return_request_sends_the_sale_idempotency_key_and_return_lines()
    {
        var returnId = Guid.NewGuid();
        using var handler = new ReturnHandler(returnId);
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        var saleId = Guid.NewGuid();
        var saleLineId = Guid.NewGuid();

        var receipt = await client.ReturnSaleAsync(saleId,
            new SaleReturnInput("Producto sin uso", [new SaleReturnLineInput(saleLineId, 1.5m, true)]),
            "return-test-key-0001");

        Assert.Equal(returnId, receipt.ReturnId);
        Assert.Equal(12.5m, receipt.Amount);
        Assert.Equal("VES", receipt.Currency);
        Assert.Equal(saleId, handler.SaleId);
        Assert.Equal("return-test-key-0001", handler.IdempotencyKey);
        Assert.Equal(saleLineId, handler.LineId);
        Assert.Equal(1.5m, handler.Quantity);
        Assert.True(handler.Restock);
    }

    private sealed class ReturnHandler(Guid returnId) : HttpMessageHandler
    {
        public Guid SaleId { get; private set; }
        public Guid LineId { get; private set; }
        public decimal Quantity { get; private set; }
        public bool Restock { get; private set; }
        public string? IdempotencyKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.StartsWith("/api/v1/sales/", request.RequestUri!.AbsolutePath);
            SaleId = Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[4]);
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("Producto sin uso", payload.RootElement.GetProperty("reason").GetString());
            var line = payload.RootElement.GetProperty("lines")[0];
            LineId = line.GetProperty("saleLineId").GetGuid();
            Quantity = line.GetProperty("quantity").GetDecimal();
            Restock = line.GetProperty("restock").GetBoolean();

            var body = JsonSerializer.Serialize(new { returnId, amount = 12.5m, currency = "VES", replayed = false });
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
