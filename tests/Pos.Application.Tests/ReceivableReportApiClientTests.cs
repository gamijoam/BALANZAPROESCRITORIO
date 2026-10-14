using System.Net;
using System.Net.Http.Json;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Application.Tests;

public sealed class ReceivableReportApiClientTests
{
    [Fact]
    public async Task Receivable_report_calls_send_paged_utc_queries_and_deserialize_rows()
    {
        using var handler = new ReportRequestHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", handler.TerminalId);
        handler.Requests.Clear();

        var balances = await client.GetReceivableBalanceReportPageAsync(page: 2, pageSize: 40);
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        var movements = await client.GetReceivableMovementReportPageAsync(from, to, "VES", page: 3, pageSize: 25);

        Assert.Equal(101, balances.TotalCount);
        Assert.Equal(40, balances.Customers.Single().BalanceUsd);
        Assert.Equal("Ana", balances.Customers.Single().CustomerName);
        Assert.Single(movements.Movements);
        Assert.Equal("Payment", movements.Movements[0].EntryType);
        Assert.Equal("VES", movements.Movements[0].OriginalCurrency);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v1/reports/receivables/balances?page=2&pageSize=40", handler.Requests[0]);
        Assert.Contains("/api/v1/reports/receivables/movements?", handler.Requests[1], StringComparison.Ordinal);
        Assert.Contains("fromUtc=2026-10-01T00%3A00%3A00.0000000Z", handler.Requests[1], StringComparison.Ordinal);
        Assert.Contains("toUtc=2026-10-04T00%3A00%3A00.0000000Z", handler.Requests[1], StringComparison.Ordinal);
        Assert.Contains("currency=VES&page=3&pageSize=25", handler.Requests[1], StringComparison.Ordinal);
    }

    private sealed class ReportRequestHandler : HttpMessageHandler
    {
        public Guid TerminalId { get; } = Guid.NewGuid();
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.RequestUri.AbsolutePath == "/api/v1/auth/login")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        operatorId = Guid.NewGuid(), terminalId = TerminalId,
                        expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token"
                    })
                });
            }

            Requests.Add(path);
            if (request.RequestUri.AbsolutePath == "/api/v1/reports/receivables/balances")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ReceivableBalanceReportPage(2, 40, 101, 900m,
                    [new ReceivableBalanceReportRow(Guid.NewGuid(), "Ana", 100m, 40m, 60m, 36000m, DateTime.UtcNow)]))
                });
            }

            Assert.Equal("/api/v1/reports/receivables/movements", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ReceivableMovementReportPage(3, 25, 51,
                [new ReceivableMovementReportRow(Guid.NewGuid(), Guid.NewGuid(), "Ana", null, "Payment", 2m,
                    1800m, "VES", 0.00111111m, "Abono", "supervisor", Guid.NewGuid(), DateTime.UtcNow)]))
            });
        }
    }
}
