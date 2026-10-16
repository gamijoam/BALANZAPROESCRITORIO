using Microsoft.AspNetCore.Http;
using Pos.Server;

namespace Pos.Integration.Tests;

public sealed class PosLicenseRequestGateTests
{
    [Theory]
    [InlineData("GET", "/api/v1/sales")]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("POST", "/api/v1/license/import")]
    [InlineData("POST", "/api/v1/sales")]
    [InlineData("POST", "/api/v1/cash-sessions/39c1657a-49b4-4c1a-9f56-7c3e0eb788d5/close")]
    [InlineData("POST", "/api/v1/sales/39c1657a-49b4-4c1a-9f56-7c3e0eb788d5/returns")]
    [InlineData("POST", "/api/v1/customers/39c1657a-49b4-4c1a-9f56-7c3e0eb788d5/payments")]
    [InlineData("POST", "/api/v1/terminals/39c1657a-49b4-4c1a-9f56-7c3e0eb788d5/unpair")]
    public void IsAllowedWhileLocked_AllowsReadCloseAndRecovery(string method, string path)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;

        Assert.True(PosLicenseRequestGate.IsAllowedWhileLocked(request));
    }

    [Theory]
    [InlineData("POST", "/api/v1/cash-sessions")]
    [InlineData("POST", "/api/v1/cash-sessions/current/claim")]
    [InlineData("PATCH", "/api/v1/products/39c1657a-49b4-4c1a-9f56-7c3e0eb788d5")]
    [InlineData("POST", "/api/v1/terminals/pair")]
    public void IsAllowedWhileLocked_BlocksNewCommercialWrites(string method, string path)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;

        Assert.False(PosLicenseRequestGate.IsAllowedWhileLocked(request));
    }
}

public sealed class PosLicenseCheckoutPolicyTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Locked_license_allows_checkout_for_a_workspace_started_before_grace_ended()
    {
        var createdAt = ExpiresAt.AddDays(2).AddTicks(-1).UtcDateTime;

        Assert.True(PosLicenseCheckoutPolicy.CanCompleteLockedCheckout(
            "Locked", ExpiresAt, createdAt, workspaceMatchesTerminal: true, existingIdempotencyKey: false));
    }

    [Fact]
    public void Locked_license_blocks_a_workspace_created_at_or_after_grace_ended()
    {
        var createdAt = ExpiresAt.AddDays(2).UtcDateTime;

        Assert.False(PosLicenseCheckoutPolicy.CanCompleteLockedCheckout(
            "Locked", ExpiresAt, createdAt, workspaceMatchesTerminal: true, existingIdempotencyKey: false));
    }

    [Fact]
    public void Locked_license_allows_only_replays_of_an_existing_idempotency_key_without_an_old_cart()
    {
        Assert.True(PosLicenseCheckoutPolicy.CanCompleteLockedCheckout(
            "Locked", ExpiresAt, workspaceCreatedAtUtc: null, workspaceMatchesTerminal: false, existingIdempotencyKey: true));
        Assert.False(PosLicenseCheckoutPolicy.CanCompleteLockedCheckout(
            "Locked", ExpiresAt, workspaceCreatedAtUtc: null, workspaceMatchesTerminal: false, existingIdempotencyKey: false));
    }

    [Fact]
    public void Locked_license_does_not_allow_a_cart_from_another_terminal()
    {
        Assert.False(PosLicenseCheckoutPolicy.CanCompleteLockedCheckout(
            "Locked", ExpiresAt, ExpiresAt.AddDays(1).UtcDateTime,
            workspaceMatchesTerminal: false, existingIdempotencyKey: false));
    }
}
