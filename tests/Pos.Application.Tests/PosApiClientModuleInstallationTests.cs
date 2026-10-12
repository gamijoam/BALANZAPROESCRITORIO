using System.Net;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientModuleInstallationTests
{
    [Fact]
    public async Task Install_module_sends_authenticated_multipart_bundle_and_returns_restart_state()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-upload-").FullName;
        var packagePath = Path.Combine(root, "inventory.zip");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        using var handler = new ModuleInstallHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        try
        {
            var plan = await client.PlanModuleInstallationAsync("inventory", [packagePath]);
            var result = await client.InstallModuleAsync("inventory", [packagePath]);

            Assert.True(plan.PlanValid);
            Assert.Equal("inventory", plan.InstallationOrder.Single().Id);
            Assert.Equal("inventory", result.RequestedModuleId);
            Assert.Equal("RestartPending", result.Status);
            Assert.True(result.RestartRequired);
            Assert.Equal("Bearer module-token", handler.Authorization);
            Assert.Equal("inventory", handler.RequestedModuleId);
            Assert.Equal(1, handler.PackageCount);
            Assert.Equal(2, handler.BundleRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Deactivate_module_calls_protected_module_route()
    {
        using var handler = new ModuleInstallHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        await client.DeactivateModuleAsync("example-status");

        Assert.Equal("/api/v1/modules/example-status/deactivate", handler.DeactivateRoute);
        Assert.Equal("Bearer module-token", handler.Authorization);
    }

    [Fact]
    public async Task Reactivate_module_calls_protected_activation_route_without_uploading_the_package_again()
    {
        using var handler = new ModuleInstallHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        await client.ActivateModuleAsync("example-status");

        Assert.Equal("/api/v1/modules/example-status/activate", handler.ActivateRoute);
        Assert.Equal("Bearer module-token", handler.Authorization);
        Assert.Equal(0, handler.BundleRequests);
    }

    [Fact]
    public async Task Get_module_route_encodes_query_values_and_uses_the_authenticated_client()
    {
        using var handler = new ModuleQueryHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        var response = await client.GetModuleAsync<List<WarehouseStockClientInfo>>("warehouses", "warehouses/abc/stock",
            new Dictionary<string, string> { ["includeInactive"] = "true", ["search"] = "a & b" });

        Assert.Empty(response!);
        Assert.Equal("Bearer module-token", handler.Authorization);
    }

    [Fact]
    public async Task Plan_module_throws_the_server_problem_detail_instead_of_deserializing_an_error_as_an_empty_plan()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-plan-error-").FullName;
        var packagePath = Path.Combine(root, "warehouses.zip");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(new ModulePlanProblemHandler()));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        try
        {
            var exception = await Assert.ThrowsAsync<PosApiException>(() => client.PlanModuleInstallationAsync("warehouses", [packagePath]));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
            Assert.Equal("La confianza de publicadores de módulos no está configurada.", exception.Detail);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Plan_module_preserves_the_server_diagnostic_id_when_an_unexpected_error_occurs()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-plan-diagnostic-").FullName;
        var packagePath = Path.Combine(root, "warehouses.zip");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(new ModulePlanDiagnosticHandler()));
        await client.LoginAsync("admin", "4826", Guid.NewGuid());

        try
        {
            var exception = await Assert.ThrowsAsync<PosApiException>(() => client.PlanModuleInstallationAsync("warehouses", [packagePath]));

            Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
            Assert.Equal("module_plan_failed", exception.Code);
            Assert.Equal("abc123", exception.DiagnosticId);
            Assert.Equal("No se pudo validar el paquete.", exception.Detail);
            Assert.Contains("abc123", PosUserMessage.ForModuleException(exception));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ModulePlanDiagnosticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(),
                        expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "module-token"
                    }), System.Text.Encoding.UTF8, "application/json")
                });

            Assert.Equal("/api/v1/modules/plan", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"title\":\"Error al validar el módulo\",\"status\":500,\"detail\":\"No se pudo validar el paquete.\",\"code\":\"module_plan_failed\",\"diagnosticId\":\"abc123\"}", System.Text.Encoding.UTF8, "application/problem+json")
            });
        }
    }

    private sealed class ModuleQueryHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(),
                        expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "module-token"
                    }), System.Text.Encoding.UTF8, "application/json")
                });
            Authorization = request.Headers.Authorization?.ToString();
            Assert.Equal("/api/v1/modules/warehouses/warehouses/abc/stock", request.RequestUri.AbsolutePath);
            Assert.Equal("?includeInactive=true&search=a%20%26%20b", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class ModulePlanProblemHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(),
                        expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "module-token"
                    }), System.Text.Encoding.UTF8, "application/json")
                });

            Assert.Equal("/api/v1/modules/plan", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"title\":\"Service Unavailable\",\"status\":503,\"detail\":\"La confianza de publicadores de módulos no está configurada.\"}", System.Text.Encoding.UTF8, "application/problem+json")
            });
        }
    }

    private sealed class ModuleInstallHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? RequestedModuleId { get; private set; }
        public int PackageCount { get; private set; }
        public int BundleRequests { get; private set; }
        public string? DeactivateRoute { get; private set; }
        public string? ActivateRoute { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Json(new { operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(), expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "module-token" });
            if (request.RequestUri.AbsolutePath.EndsWith("/deactivate", StringComparison.Ordinal))
            {
                DeactivateRoute = request.RequestUri.AbsolutePath;
                Authorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/activate", StringComparison.Ordinal))
            {
                ActivateRoute = request.RequestUri.AbsolutePath;
                Authorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert.Contains(request.RequestUri.AbsolutePath, new[] { "/api/v1/modules/plan", "/api/v1/modules/install" });
            BundleRequests++;
            Authorization = request.Headers.Authorization?.ToString();
            Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("inventory.zip", body, StringComparison.Ordinal);
            RequestedModuleId = body.Contains("inventory", StringComparison.Ordinal) ? "inventory" : null;
            PackageCount = body.Contains("inventory.zip", StringComparison.Ordinal) ? 1 : 0;
            if (request.RequestUri.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal))
                return Json(new { requestedModuleId = "inventory", planValid = true,
                    installationOrder = new[] { new { id = "inventory", version = "1.0.0", signerKeyId = "official", entitlement = (string?)null,
                        clientApi = new { min = "1.0.0", maxExclusive = "2.0.0" }, hasRuntimeEntry = true,
                        terminalCompatibility = new { isCompatible = true, registeredTerminalCount = 1, unreportedTerminalCount = 0, incompatibleVersions = Array.Empty<string>() },
                        permissions = Array.Empty<string>(), migrations = Array.Empty<string>() } } });
            return Json(new { requestedModuleId = "inventory", status = "RestartPending", restartRequired = true,
                backupPath = "C:\\ProgramData\\POS\\Backups\\test.backup", backupSha256 = new string('a', 64), packages = Array.Empty<object>(), migrations = Array.Empty<object>() });
        }

        private static HttpResponseMessage Json<T>(T content) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(content), System.Text.Encoding.UTF8, "application/json") };
    }
}
