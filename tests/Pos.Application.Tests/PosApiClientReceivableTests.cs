using System.Net;
using System.Net.Http.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientReceivableTests
{
    [Fact]
    public async Task Receivable_opening_balance_uses_the_authenticated_api_contract()
    {
        var customerId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var reason = "Migración de saldo al 13/10/2026";
        var idempotencyKey = $"legacy-opening-{Guid.NewGuid():N}";
        using var handler = new ReceivableHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId, expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token" })
                });

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/v1/customers/{customerId}/receivables/opening-balance", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var json = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(1260.0513m, json.RootElement.GetProperty("amountUsd").GetDecimal());
            Assert.Equal(reason, json.RootElement.GetProperty("reason").GetString());
            Assert.Equal(idempotencyKey, json.RootElement.GetProperty("idempotencyKey").GetString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { id = Guid.NewGuid(), amount = 1260.0513m, replayed = false })
            });
        });
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler, disposeHandler: false));
        await client.LoginAsync("api-test", "test-pin", terminalId);

        await client.RecordReceivableOpeningBalanceAsync(customerId, 1260.0513m, reason, idempotencyKey);
    }

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
        await client.LoginAsync("api-test", "test-pin", terminalId);

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
