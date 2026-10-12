using System.IO;

namespace Pos.Desktop.Presentation;

/// <summary>Detects whether a responding local server belongs to the desktop package being launched.</summary>
public static class PosServerRestartPolicy
{
    public static bool RequiresRestart(string expectedExecutablePath, string runningExecutablePath,
        DateTime executableLastWriteUtc, DateTime processStartedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runningExecutablePath);

        if (!string.Equals(Path.GetFullPath(expectedExecutablePath), Path.GetFullPath(runningExecutablePath),
                StringComparison.OrdinalIgnoreCase))
            return true;

        return executableLastWriteUtc.ToUniversalTime() > processStartedAtUtc.ToUniversalTime().AddSeconds(2);
    }
}
