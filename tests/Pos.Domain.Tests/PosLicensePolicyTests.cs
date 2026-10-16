using Pos.Domain.Licensing;

namespace Pos.Domain.Tests;

public sealed class PosLicensePolicyTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly PosLicense License = new(
        1,
        "LIC-2026-00145",
        "J-12345678-9",
        "Comercio Ejemplo C.A.",
        Guid.Parse("39c1657a-49b4-4c1a-9f56-7c3e0eb788d5"),
        "PRO_MULTICAJA",
        3,
        ["pos.core", "warehouses"],
        new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
        Expiry);

    [Fact]
    public void Evaluate_ReturnsActiveBeforeExpiry()
    {
        Assert.Equal(PosLicenseState.Active, PosLicensePolicy.Evaluate(License, Expiry.AddTicks(-1)));
    }

    [Fact]
    public void Evaluate_StartsGraceAtExpiry()
    {
        Assert.Equal(PosLicenseState.Grace, PosLicensePolicy.Evaluate(License, Expiry));
    }

    [Fact]
    public void Evaluate_KeepsGraceUntilTwoDaysAfterExpiry()
    {
        Assert.Equal(PosLicenseState.Grace, PosLicensePolicy.Evaluate(License, Expiry.AddDays(2).AddTicks(-1)));
    }

    [Fact]
    public void Evaluate_LocksAtExactGraceEnd()
    {
        Assert.Equal(PosLicenseState.Locked, PosLicensePolicy.Evaluate(License, Expiry.AddDays(2)));
    }

    [Fact]
    public void Evaluate_RequiresUtcCurrentTime()
    {
        Assert.Throws<ArgumentException>(() => PosLicensePolicy.Evaluate(
            License,
            new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.FromHours(-4))));
    }

    [Fact]
    public void CanPairTerminal_AllowsRotatingCredentialForExistingActiveTerminalAtLimit()
    {
        Assert.True(PosLicensePolicy.CanPairTerminal(activeTerminalCount: 3, terminalAlreadyActive: true, maximumTerminals: 3));
    }

    [Fact]
    public void CanPairTerminal_RejectsNewTerminalAtLimit()
    {
        Assert.False(PosLicensePolicy.CanPairTerminal(activeTerminalCount: 3, terminalAlreadyActive: false, maximumTerminals: 3));
    }

    [Fact]
    public void CanPairTerminal_AllowsNewTerminalBelowLimit()
    {
        Assert.True(PosLicensePolicy.CanPairTerminal(activeTerminalCount: 2, terminalAlreadyActive: false, maximumTerminals: 3));
    }
}
