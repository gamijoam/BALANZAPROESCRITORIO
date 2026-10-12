using System.Net;
using System.Text;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientModuleManagementAccessTests
{
    [Fact]
    public async Task Module_management_access_reports_allowed_and_pin_required_for_authenticated_operator()
    {
        using var handler = new ManagementAccessHandler(canOpenManagement: true, requiresPin: true);
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("cashier", "4826", Guid.NewGuid());

        var access = await client.GetModuleManagementAccessAsync();

        Assert.True(access.CanOpenManagement);
        Assert.True(access.RequiresAdministratorPin);
        Assert.Equal("Bearer module-token", handler.Authorization);
        Assert.Equal("/api/v1/modules/management-access", handler.RequestPath);
    }

    [Fact]
    public async Task Module_management_access_reports_denied_for_operator_without_permission()
    {
        using var handler = new ManagementAccessHandler(canOpenManagement: false, requiresPin: false);
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("cashier", "4826", Guid.NewGuid());

        var access = await client.GetModuleManagementAccessAsync();

        Assert.False(access.CanOpenManagement);
        Assert.False(access.RequiresAdministratorPin);
    }

    private sealed class ManagementAccessHandler : HttpMessageHandler
    {
        private readonly bool _canOpenManagement;
        private readonly bool _requiresPin;

        public ManagementAccessHandler(bool canOpenManagement, bool requiresPin)
        {
            _canOpenManagement = canOpenManagement;
            _requiresPin = requiresPin;
        }

        public string? Authorization { get; private set; }
        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(Json(new
                {
                    operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(),
                    expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "module-token"
                }));

            Authorization = request.Headers.Authorization?.ToString();
            RequestPath = request.RequestUri.AbsolutePath;
            Assert.Equal("GET", request.Method.Method);
            return Task.FromResult(Json(new { canOpenManagement = _canOpenManagement, requiresAdministratorPin = _requiresPin }));
        }

        private static HttpResponseMessage Json<T>(T content) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json") };
    }
}
