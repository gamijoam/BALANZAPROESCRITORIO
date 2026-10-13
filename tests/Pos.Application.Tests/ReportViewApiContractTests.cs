using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Application.Tests;

public sealed class ReportViewApiContractTests
{
    [Fact]
    public async Task Save_report_view_sends_the_definition_inside_the_view_property()
    {
        using var handler = new ReportViewRequestHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", handler.TerminalId);

        var definition = new SavedReportViewDefinition("VISTA CLIENTE/PAGOS",
            [new("sale", true, 0), new("client", true, 1), new("payments", true, 2)],
            new Dictionary<string, string> { ["currency"] = "USD" });
        await client.SaveReportViewAsync("reports.sales.detail", definition);

        using var payload = JsonDocument.Parse(handler.SavePayload!);
        var root = payload.RootElement;
        Assert.Equal("reports.sales.detail", root.GetProperty("reportId").GetString());
        var view = root.GetProperty("view");
        Assert.Equal("VISTA CLIENTE/PAGOS", view.GetProperty("name").GetString());
        Assert.Equal(3, view.GetProperty("columns").GetArrayLength());
        Assert.Equal("USD", view.GetProperty("filters").GetProperty("currency").GetString());
    }

    private sealed class ReportViewRequestHandler : HttpMessageHandler
    {
        public Guid TerminalId { get; } = Guid.NewGuid();
        public string? SavePayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        operatorId = Guid.NewGuid(), terminalId = TerminalId,
                        expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token"
                    })
                };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/reports/views", request.RequestUri.AbsolutePath);
            SavePayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var payload = JsonDocument.Parse(SavePayload);
            var view = payload.RootElement.GetProperty("view");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SavedReportViewInfo(Guid.NewGuid(),
                    payload.RootElement.GetProperty("reportId").GetString()!,
                    view.GetProperty("name").GetString()!,
                    view.GetProperty("columns").Deserialize<List<ReportColumnLayout>>()!,
                    view.GetProperty("filters").Deserialize<Dictionary<string, string>>()!,
                    DateTime.UtcNow))
            };
        }
    }
}
