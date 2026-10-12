using System.IO;

namespace Pos.Desktop.Presentation;

/// <summary>Finds pg_dump, pg_restore, and psql without requiring a machine-wide PATH change.</summary>
public static class PostgreSqlBinaryDirectoryResolver
{
    public static string? Resolve(string? configuredDirectory, IEnumerable<string> installedBinaryDirectories, Func<string, bool> hasRequiredTools)
    {
        ArgumentNullException.ThrowIfNull(installedBinaryDirectories);
        ArgumentNullException.ThrowIfNull(hasRequiredTools);

        if (!string.IsNullOrWhiteSpace(configuredDirectory) && hasRequiredTools(configuredDirectory))
            return configuredDirectory;

        return installedBinaryDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path) && hasRequiredTools(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GetVersion)
            .FirstOrDefault();
    }

    public static bool HasRequiredTools(string directory) =>
        File.Exists(Path.Combine(directory, "pg_dump.exe")) &&
        File.Exists(Path.Combine(directory, "pg_restore.exe")) &&
        File.Exists(Path.Combine(directory, "psql.exe"));

    private static Version GetVersion(string binaryDirectory)
    {
        var versionDirectory = Path.GetFileName(Path.GetDirectoryName(binaryDirectory));
        if (int.TryParse(versionDirectory, out var major)) return new Version(major, 0);
        return Version.TryParse(versionDirectory, out var version) ? version : new Version(0, 0);
    }
}
