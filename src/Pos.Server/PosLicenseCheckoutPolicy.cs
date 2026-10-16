using Pos.Domain.Licensing;

namespace Pos.Server;

internal static class PosLicenseCheckoutPolicy
{
    public static bool CanCompleteLockedCheckout(string licenseState, DateTimeOffset? expiresAtUtc,
        DateTime? workspaceCreatedAtUtc, bool workspaceMatchesTerminal, bool existingIdempotencyKey)
    {
        if (licenseState is "Active" or "Grace") return true;
        if (licenseState != "Locked") return false;
        if (existingIdempotencyKey) return true;
        if (expiresAtUtc is not { } expiration || expiration.Offset != TimeSpan.Zero || workspaceCreatedAtUtc is null || !workspaceMatchesTerminal)
            return false;

        DateTimeOffset graceEndsAtUtc;
        try { graceEndsAtUtc = expiration.Add(PosLicensePolicy.GracePeriod); }
        catch (ArgumentOutOfRangeException) { graceEndsAtUtc = DateTimeOffset.MaxValue; }

        var createdAtUtc = new DateTimeOffset(DateTime.SpecifyKind(workspaceCreatedAtUtc.Value, DateTimeKind.Utc));
        return createdAtUtc < graceEndsAtUtc;
    }
}
