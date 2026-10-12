using System.Net;
using System.Net.Http.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientReceivableTests
{
    [Fact]
    public async Task Receivable_query_deserializes_sale_items_and_current_ves_equivalent()
    {
        var customerId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        using var handler = new ReceivableHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId, terminalId, expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token" })
                });

            Assert.Equal($"/api/v1/customers/{customerId}/receivables", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = customerId, name = "Cliente", currency = "USD", creditLimit = 100m, balance = 8.50m,
                    balanceVesEquivalent = 340m, currentUsdToVesRate = 40m,
                    entries = new[]
                    {
                        new
                        {
                            id = Guid.NewGuid(), customerId, saleId, entryType = "Charge", amount = 8.50m, currency = "USD",
                            reason = "Venta", actorId = operatorId, terminalId, occurredAtUtc = DateTime.UtcNow,
                            originalAmount = 340m, originalCurrency = "VES", appliedExchangeRate = 0.025m,
                            saleItems = new[] { new { sku = "PAN-1", description = "Harina PAN", quantity = 1m, amountUsd = 8.50m } }
                        }
                    }
                })
            });
        });
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler, disposeHandler: false));
        await client.LoginAsync("demo", "4826", terminalId);

        var account = await client.GetReceivablesAsync(customerId);

        Assert.Equal(340m, account.BalanceVesEquivalent);
        var charge = Assert.Single(account.Entries);
        var item = Assert.Single(charge.SaleItems);
        Assert.Equal("Harina PAN", item.Description);
        Assert.Equal(8.50m, item.AmountUsd);
    }

    private sealed class ReceivableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }
}
