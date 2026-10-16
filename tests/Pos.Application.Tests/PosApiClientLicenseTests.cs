using System.Net;
using System.Text;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientLicenseTests
{
    [Fact]
    public async Task License_status_is_read_from_the_authenticated_server()
    {
        using var handler = new LicenseHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        var status = await client.GetLicenseStatusAsync();

        Assert.Equal("Active", status.State);
        Assert.Equal("license-1", status.LicenseId);
        Assert.Equal("Bearer license-token", handler.Authorization);
        Assert.Equal("GET", handler.Method);
        Assert.Equal("/api/v1/license/status", handler.Path);
    }

    [Fact]
    public async Task License_import_sends_the_signed_document_to_the_authenticated_server()
    {
        using var handler = new LicenseHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());
        var document = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");

        var status = await client.ImportLicenseAsync(document);

        Assert.Equal("Active", status.State);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/api/v1/license/import", handler.Path);
        Assert.Equal(Convert.ToBase64String(document), handler.ImportedDocumentBase64);
        Assert.Equal("Bearer license-token", handler.Authorization);
    }

    private sealed class LicenseHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? ImportedDocumentBase64 { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Json(new
                {
                    operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(),
                    expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "license-token"
                });

            Authorization = request.Headers.Authorization?.ToString();
            Method = request.Method.Method;
            Path = request.RequestUri.AbsolutePath;
            if (Method == "POST")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                ImportedDocumentBase64 = payload.RootElement.GetProperty("documentBase64").GetString();
            }

            return Json(new
            {
                installationId = Guid.NewGuid(), state = "Active", licenseId = "license-1", customerName = "Comercio",
                plan = "pro", maxTerminals = 3, expiresAtUtc = DateTimeOffset.UtcNow.AddDays(30),
                entitlements = new[] { "warehouses" }, diagnostic = (string?)null
            });
        }

        private static HttpResponseMessage Json<T>(T content) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json") };
    }
}
