namespace Pos.Desktop.Presentation;

public sealed record PosInstallationStartupPlan(Uri ApiBaseAddress, bool StartLocalServer, string? ListenUrls = null)
{
    public static PosInstallationStartupPlan Resolve(PosInstallationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if ((profile.Mode is PosInstallationMode.Standalone or PosInstallationMode.Server) && !profile.DatabaseConfigured)
            throw new InvalidOperationException("Primero configura y valida PostgreSQL para iniciar el servicio POS.");
        return profile.Mode switch
        {
            PosInstallationMode.Standalone => new PosInstallationStartupPlan(new Uri("http://127.0.0.1:5155/"), true),
            PosInstallationMode.ClientTerminal => new PosInstallationStartupPlan(new Uri(profile.ApiBaseUrl), false),
            PosInstallationMode.Server => new PosInstallationStartupPlan(new Uri(profile.ApiBaseUrl), true, profile.ApiBaseUrl.TrimEnd('/')),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }
}
