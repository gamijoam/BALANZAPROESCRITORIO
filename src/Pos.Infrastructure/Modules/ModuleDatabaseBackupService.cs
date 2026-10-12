using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Pos.Infrastructure.Modules;

public sealed class ModuleDatabaseBackupException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record PostgresToolResult(int ExitCode, string StandardOutput, string StandardError);

public interface IPostgresToolRunner
{
    Task<PostgresToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default);
}

public sealed record VerifiedModuleDatabaseBackup(string BackupPath, string ChecksumPath, string Sha256, DateTime CreatedAtUtc);

/// <summary>Creates a custom-format pg_dump, verifies it with pg_restore, and publishes it atomically.</summary>
public sealed class ModuleDatabaseBackupService(IPostgresToolRunner toolRunner)
{
    private const long MaxBackupBytes = 32L * 1024 * 1024 * 1024;

    public async Task<VerifiedModuleDatabaseBackup> CreateVerifiedBackupAsync(
        string connectionString,
        string postgresBinaryDirectory,
        string backupDirectory,
        string moduleStorageDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolRunner);
        NpgsqlConnectionStringBuilder connection;
        try { connection = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { throw new ModuleDatabaseBackupException("La conexión PostgreSQL configurada no es válida.", exception); }

        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(connection.Database) || string.IsNullOrWhiteSpace(connection.Username) ||
            string.IsNullOrEmpty(connection.Password))
            throw new ModuleDatabaseBackupException("La conexión PostgreSQL debe incluir host, puerto, base, usuario y contraseña.");
        if (!Path.IsPathFullyQualified(postgresBinaryDirectory) || !Path.IsPathFullyQualified(backupDirectory) ||
            !Path.IsPathFullyQualified(moduleStorageDirectory))
            throw new ModuleDatabaseBackupException("Las rutas de PostgreSQL, respaldo y módulos deben ser absolutas.");

        var binaryDirectory = Path.GetFullPath(postgresBinaryDirectory);
        var backups = Path.GetFullPath(backupDirectory);
        var modules = Path.GetFullPath(moduleStorageDirectory);
        if (PathsOverlap(backups, modules))
            throw new ModuleDatabaseBackupException("Los respaldos deben quedar fuera del almacén de módulos y de sus carpetas padre.");

        var dumpExecutable = Path.Combine(binaryDirectory, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
        var restoreExecutable = Path.Combine(binaryDirectory, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
        if (!File.Exists(dumpExecutable) || !File.Exists(restoreExecutable))
            throw new ModuleDatabaseBackupException("No se encontraron pg_dump y pg_restore en la carpeta configurada de PostgreSQL.");

        EnsureSafeDirectoryPath(backups);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var fileName = $"pos-module-preinstall-{stamp}-{Guid.NewGuid():N}.backup";
        var finalPath = Path.Combine(backups, fileName);
        var partialPath = finalPath + ".partial";
        var finalChecksumPath = finalPath + ".sha256";
        var partialChecksumPath = finalChecksumPath + ".partial";
        var published = false;

        try
        {
            var passwordEnvironment = new Dictionary<string, string?> { ["PGPASSWORD"] = connection.Password };
            var dumpArguments = new[]
            {
                "--no-password", "--format=custom", "--compress=6", "--host", connection.Host,
                "--port", connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--username", connection.Username, "--dbname", connection.Database, "--file", partialPath
            };
            var dump = await toolRunner.RunAsync(dumpExecutable, dumpArguments, passwordEnvironment, cancellationToken);
            if (dump.ExitCode != 0)
                throw new ModuleDatabaseBackupException($"pg_dump terminó con código {dump.ExitCode}; la instalación del módulo se cancela.");
            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length is <= 0 or > MaxBackupBytes)
                throw new ModuleDatabaseBackupException("pg_dump no produjo un respaldo completo de tamaño válido.");

            var verify = await toolRunner.RunAsync(restoreExecutable, ["--list", partialPath], passwordEnvironment, cancellationToken);
            if (verify.ExitCode != 0)
                throw new ModuleDatabaseBackupException($"pg_restore rechazó el respaldo con código {verify.ExitCode}; la instalación del módulo se cancela.");

            var sha256 = await HashFileAsync(partialPath, cancellationToken);
            var checksum = $"{sha256.ToUpperInvariant()}  {fileName}{Environment.NewLine}";
            await File.WriteAllTextAsync(partialChecksumPath, checksum, new UTF8Encoding(false), cancellationToken);
            File.Move(partialPath, finalPath);
            File.Move(partialChecksumPath, finalChecksumPath);
            published = true;
            return new VerifiedModuleDatabaseBackup(finalPath, finalChecksumPath, sha256, DateTime.UtcNow);
        }
        catch (ModuleDatabaseBackupException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ModuleDatabaseBackupException("No se pudo crear y verificar el respaldo previo a la instalación.", exception);
        }
        finally
        {
            DeleteIfPresent(partialPath);
            DeleteIfPresent(partialChecksumPath);
            if (!published)
            {
                DeleteIfPresent(finalPath);
                DeleteIfPresent(finalChecksumPath);
            }
        }
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrChild(first, second) || IsSameOrChild(second, first);

    private static bool IsSameOrChild(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison)) return true;
        var parentPrefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(parentPrefix, comparison);
    }

    private static void EnsureSafeDirectoryPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) throw new ModuleDatabaseBackupException("La ruta del respaldo no tiene una raíz válida.");
        var current = root;
        EnsureDirectory(current);
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureDirectory(current);
        }
    }

    private static void EnsureDirectory(string path)
    {
        if (File.Exists(path)) throw new ModuleDatabaseBackupException("La ruta del respaldo contiene un archivo donde se esperaba una carpeta.");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ModuleDatabaseBackupException("La ruta del respaldo contiene un enlace o punto de reanálisis.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void DeleteIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Runs PostgreSQL command-line tools without putting credentials in their arguments or logs.</summary>
public sealed class ProcessPostgresToolRunner : IPostgresToolRunner
{
    private const int MaxCapturedErrorCharacters = 8192;

    public async Task<PostgresToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new ModuleDatabaseBackupException("No se pudo iniciar una herramienta de PostgreSQL.");
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxCapturedErrorCharacters, cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await Task.WhenAll(stdoutTask, stderrTask);
            return new PostgresToolResult(process.ExitCode, string.Empty, stderrTask.Result);
        }
        catch (ModuleDatabaseBackupException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new ModuleDatabaseBackupException("No se pudo ejecutar la herramienta PostgreSQL requerida.", exception);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var output = new StringBuilder(Math.Min(maxCharacters, 2048));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            var keep = Math.Min(read, maxCharacters - output.Length);
            if (keep > 0) output.Append(buffer, 0, keep);
        }
        return output.ToString();
    }
}
