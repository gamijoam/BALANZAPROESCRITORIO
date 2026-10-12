using System.Security.Cryptography;
using Pos.Infrastructure.Modules;

namespace Pos.Integration.Tests;

public sealed class ModuleDatabaseBackupServiceTests
{
    [Fact]
    public async Task CreateVerifiedBackup_writes_custom_dump_checksum_and_hides_password_from_arguments()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-backup-").FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "postgres-bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "pg_dump.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "pg_restore.exe"), string.Empty);
        var runner = new FakePostgresToolRunner();
        try
        {
            var result = await new ModuleDatabaseBackupService(runner).CreateVerifiedBackupAsync(
                "Host=localhost;Port=5432;Database=pos;Username=pos_app;Password=top-secret",
                bin, Path.Combine(root, "backups"), Path.Combine(root, "modules"));

            Assert.True(File.Exists(result.BackupPath));
            Assert.True(File.Exists(result.ChecksumPath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(result.BackupPath))).ToLowerInvariant(), result.Sha256);
            Assert.Equal(2, runner.Invocations.Count);
            Assert.DoesNotContain(runner.Invocations.SelectMany(call => call.Arguments), arg => arg.Contains("top-secret", StringComparison.Ordinal));
            Assert.Equal("top-secret", runner.Invocations[0].Environment["PGPASSWORD"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CreateVerifiedBackup_cleans_partial_files_when_dump_or_verification_fails()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-backup-").FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "postgres-bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "pg_dump.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "pg_restore.exe"), string.Empty);
        var runner = new FakePostgresToolRunner { RestoreListExitCode = 1 };
        try
        {
            await Assert.ThrowsAsync<ModuleDatabaseBackupException>(() =>
                new ModuleDatabaseBackupService(runner).CreateVerifiedBackupAsync(
                    "Host=localhost;Port=5432;Database=pos;Username=pos_app;Password=top-secret",
                    bin, Path.Combine(root, "backups"), Path.Combine(root, "modules")));

            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups")));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "backups")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CreateVerifiedBackup_cancels_when_pg_dump_returns_failure()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-backup-").FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "postgres-bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "pg_dump.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "pg_restore.exe"), string.Empty);
        var runner = new FakePostgresToolRunner { DumpExitCode = 1 };
        try
        {
            await Assert.ThrowsAsync<ModuleDatabaseBackupException>(() =>
                new ModuleDatabaseBackupService(runner).CreateVerifiedBackupAsync(
                    "Host=localhost;Port=5432;Database=pos;Username=pos_app;Password=top-secret",
                    bin, Path.Combine(root, "backups"), Path.Combine(root, "modules")));

            Assert.Single(runner.Invocations);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CreateVerifiedBackup_rejects_destination_inside_module_store_before_running_tools()
    {
        var root = Directory.CreateTempSubdirectory("pos-module-backup-").FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "postgres-bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "pg_dump.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "pg_restore.exe"), string.Empty);
        var runner = new FakePostgresToolRunner();
        try
        {
            await Assert.ThrowsAsync<ModuleDatabaseBackupException>(() =>
                new ModuleDatabaseBackupService(runner).CreateVerifiedBackupAsync(
                    "Host=localhost;Port=5432;Database=pos;Username=pos_app;Password=top-secret",
                    bin, Path.Combine(root, "modules", "backups"), Path.Combine(root, "modules")));

            Assert.Empty(runner.Invocations);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FakePostgresToolRunner : IPostgresToolRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment)> Invocations { get; } = [];
        public int RestoreListExitCode { get; init; }
        public int DumpExitCode { get; init; }

        public Task<PostgresToolResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken = default)
        {
            Invocations.Add((executable, arguments, environment));
            if (Path.GetFileName(executable).Equals("pg_dump.exe", StringComparison.OrdinalIgnoreCase))
            {
                var fileIndex = Array.IndexOf(arguments.ToArray(), "--file");
                File.WriteAllBytes(arguments[fileIndex + 1], [1, 2, 3, 4]);
                return Task.FromResult(new PostgresToolResult(DumpExitCode, string.Empty, string.Empty));
            }
            return Task.FromResult(new PostgresToolResult(RestoreListExitCode, string.Empty, string.Empty));
        }
    }
}
