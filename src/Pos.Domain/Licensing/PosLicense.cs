namespace Pos.Domain.Licensing;

/// <summary>Signed license terms installed on the local POS server.</summary>
public sealed record PosLicense(
    int SchemaVersion,
    string LicenseId,
    string CustomerRif,
    string CustomerName,
    Guid InstallationId,
    string Plan,
    int MaxTerminals,
    IReadOnlyList<string> Entitlements,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? RenewalToken = null);

public enum PosLicenseState
{
    Active,
    Grace,
    Locked
}

public static class PosLicensePolicy
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(2);

    public static bool CanPairTerminal(int activeTerminalCount, bool terminalAlreadyActive, int maximumTerminals)
    {
        if (activeTerminalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(activeTerminalCount));
        if (maximumTerminals < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTerminals));
        return terminalAlreadyActive || activeTerminalCount < maximumTerminals;
    }

    public static PosLicenseState Evaluate(PosLicense license, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("La hora actual debe estar expresada en UTC.", nameof(nowUtc));
        if (license.ExpiresAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("El vencimiento debe estar expresado en UTC.", nameof(license));

        if (nowUtc < license.ExpiresAtUtc)
            return PosLicenseState.Active;

        DateTimeOffset graceEndsAtUtc;
        try
        {
            graceEndsAtUtc = license.ExpiresAtUtc.Add(GracePeriod);
        }
        catch (ArgumentOutOfRangeException)
        {
            graceEndsAtUtc = DateTimeOffset.MaxValue;
        }

        return nowUtc < graceEndsAtUtc ? PosLicenseState.Grace : PosLicenseState.Locked;
    }
}
