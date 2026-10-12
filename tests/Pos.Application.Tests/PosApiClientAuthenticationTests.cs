using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientAuthenticationTests
{
    [Fact]
    public void Pinned_certificate_accepts_only_the_trusted_server_fingerprint()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pos.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = ServerCertificatePin.ComputeSha256(certificate);

        Assert.True(ServerCertificatePin.IsValid(fingerprint));
        Assert.True(ServerCertificatePin.Matches(fingerprint, certificate));
        Assert.False(ServerCertificatePin.Matches(new string('0', 64), certificate));
        Assert.False(ServerCertificatePin.Matches("invalid", certificate));
    }

    [Fact]
    public async Task Correct_pin_after_one_unauthorized_response_is_sent_and_accepted()
    {
        using var handler = new RetryLoginHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));

        await Assert.ThrowsAsync<PosApiException>(() => client.LoginAsync("demo", "4526", handler.TerminalId));
        Assert.Null(client.AccessToken);
        client.BaseAddress = new Uri("http://localhost:5155/");

        var result = await client.LoginAsync("demo", "4826", handler.TerminalId);

        Assert.Equal(2, handler.Attempts);
        Assert.Equal("4826", handler.LastPin);
        Assert.Equal(result.AccessToken, client.AccessToken);
    }

    [Fact]
    public async Task Current_cash_session_includes_service_calculated_expected_balances()
    {
        using var handler = new CurrentCashSessionHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("demo", "4826", Guid.NewGuid());

        var session = await client.GetCurrentCashSessionAsync();

        Assert.NotNull(session);
        Assert.Equal(125.50m, session.ExpectedUsd);
        Assert.Equal(9_500m, session.ExpectedVes);
        Assert.Equal("Bearer test-token", handler.Authorization);
    }

    [Fact]
    public async Task Client_terminal_sends_its_paired_credential_only_with_login()
    {
        using var handler = new RetryLoginHandler();
        var credential = new string('b', 64);
        using var httpClient = new HttpClient(handler);
        using var client = new PosApiClient(new Uri("https://pos.local:5155"), httpClient, credential);

        await Assert.ThrowsAsync<PosApiException>(() => client.LoginAsync("demo", "4526", handler.TerminalId));
        await client.LoginAsync("demo", "4826", handler.TerminalId);

        Assert.Equal(credential, handler.TerminalCredential);
    }

    [Fact]
    public async Task Admin_pin_is_requested_and_approval_is_bound_to_one_retried_operation()
    {
        using var handler = new AdminApprovalHandler();
        using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await client.LoginAsync("cashier", "1234", handler.TerminalId);
        var prompted = 0;
        client.RequestAdministratorPinAsync = (challenge, _) =>
        {
            prompted++;
            Assert.Equal("CloseCashSession", challenge.Permission);
            Assert.Equal("POST /api/v1/cash-sessions/" + handler.CashSessionId + "/movements", challenge.Operation);
            return Task.FromResult<AdministratorCredentials?>(new("manager", "8765"));
        };

        await client.RecordCashMovementAsync(handler.CashSessionId, new CashMovementInput(5m, "In", "Cambio", "VES"));

        Assert.Equal(1, prompted);
        Assert.Equal(2, handler.OperationAttempts);
        Assert.Equal(1, handler.AuthorizationAttempts);
    }

    private sealed class RetryLoginHandler : HttpMessageHandler
    {
        public Guid TerminalId { get; } = Guid.NewGuid();
        public int Attempts { get; private set; }
        public string? LastPin { get; private set; }
        public string? TerminalCredential { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/auth/login", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastPin = body.RootElement.GetProperty("pin").GetString();
            Assert.Equal(TerminalId, body.RootElement.GetProperty("terminalId").GetGuid());
            Assert.True(Version.TryParse(body.RootElement.GetProperty("clientVersion").GetString(), out var clientVersion));
            Assert.Equal(-1, clientVersion!.Revision);
            TerminalCredential = request.Headers.TryGetValues("X-POS-Terminal-Credential", out var credentials)
                ? credentials.Single()
                : null;
            Attempts++;
            if (Attempts == 1) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    operatorId = Guid.NewGuid(),
                    terminalId = TerminalId,
                    expiresAtUtc = DateTime.UtcNow.AddHours(1),
                    accessToken = "fresh-login-token"
                })
            };
        }
    }

    private sealed class CurrentCashSessionHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var terminalId = body.RootElement.GetProperty("terminalId").GetGuid();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId, expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token" })
                };
            }

            Assert.Equal("/api/v1/cash-sessions/current", request.RequestUri.AbsolutePath);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = Guid.NewGuid(), currency = "VES", openingBalance = 0m, openedAtUtc = DateTime.UtcNow,
                    openingBalanceUsd = 0m, openingBalanceVes = 0m, expectedUsd = 125.50m, expectedVes = 9_500m
                })
            };
        }
    }

    private sealed class AdminApprovalHandler : HttpMessageHandler
    {
        public Guid TerminalId { get; } = Guid.NewGuid();
        public Guid CashSessionId { get; } = Guid.NewGuid();
        public int OperationAttempts { get; private set; }
        public int AuthorizationAttempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId = TerminalId, expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "cashier-token" }) };
            if (request.RequestUri.AbsolutePath == "/api/v1/auth/authorize")
            {
                AuthorizationAttempts++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("manager", body.RootElement.GetProperty("administratorUsername").GetString());
                Assert.Equal("8765", body.RootElement.GetProperty("administratorPin").GetString());
                Assert.Equal(new string('a', 64), body.RootElement.GetProperty("requestFingerprint").GetString());
                Assert.Equal("cashier-token", request.Headers.Authorization!.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { approvalToken = "one-time.approval" }) };
            }
            Assert.Equal("/api/v1/cash-sessions/" + CashSessionId + "/movements", request.RequestUri.AbsolutePath);
            OperationAttempts++;
            if (OperationAttempts == 1)
                return new HttpResponseMessage((HttpStatusCode)428) { Content = JsonContent.Create(new { error = "admin_pin_required", permission = "CloseCashSession", operationId = Guid.NewGuid(), operation = request.Method + " " + request.RequestUri.PathAndQuery, requestFingerprint = new string('a', 64) }) };
            Assert.Equal("one-time.approval", request.Headers.GetValues("X-Admin-Approval").Single());
            using var retryBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(5m, retryBody.RootElement.GetProperty("amount").GetDecimal());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
