namespace Pos.Modules.Abstractions;

public static partial class ClientApiVersionParser
{
    public static bool TryParseOptional(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!SemVerPattern().IsMatch(value) || !Version.TryParse(value, out var parsed)) return false;
        version = parsed;
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SemVerPattern();
}

public sealed record ModuleTerminalCompatibilityResult(
    bool IsCompatible,
    int RegisteredTerminalCount,
    int UnreportedTerminalCount,
    IReadOnlyList<string> IncompatibleVersions);

/// <summary>Fails closed unless every registered terminal reported a client API version accepted by the module.</summary>
public sealed class ModuleTerminalCompatibilityPolicy
{
    public ModuleTerminalCompatibilityResult Evaluate(ModuleClientApiRange range, IEnumerable<Version?> terminalVersions)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(terminalVersions);
        var versions = terminalVersions.ToArray();
        var unreported = versions.Count(version => version is null);
        var incompatible = versions.Where(version => version is not null && !range.Accepts(version))
            .Select(version => version!.ToString(3)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ModuleTerminalCompatibilityResult(unreported == 0 && incompatible.Length == 0,
            versions.Length, unreported, incompatible);
    }
}
