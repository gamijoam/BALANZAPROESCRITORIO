using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Pos.Modules.Abstractions;
using Pos.Modules.Packaging;

namespace Pos.Infrastructure.Modules;

public sealed class ModuleMigrationExecutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record ModuleMigrationExecutionResult(string ModuleId, string MigrationId, string Sha256, bool AlreadyApplied);

/// <summary>Applies signed, restricted module migrations in one PostgreSQL transaction on the server.</summary>
public sealed class ModuleMigrationExecutor
{
    private const long AdvisoryLockKey = 48151623420001;
    private const int MaxPackageBytes = 512 * 1024 * 1024;
    private const int MaxMigrationBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string HistorySchema = "pos_modules";
    private const string HistoryTable = "module_migration_history";

    public async Task<IReadOnlyList<ModuleMigrationExecutionResult>> ApplyAsync(
        string connectionString,
        IReadOnlyList<string> orderedPackagePaths,
        string requestedModuleId,
        int hostApiVersion,
        ModuleTrustKeyRing trustKeyRing,
        IReadOnlySet<string> grantedEntitlements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedPackagePaths);
        ArgumentNullException.ThrowIfNull(trustKeyRing);
        ArgumentNullException.ThrowIfNull(grantedEntitlements);
        if (orderedPackagePaths.Count == 0) return [];

        var packages = new List<PreparedModule>(orderedPackagePaths.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packagePath in orderedPackagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(packagePath);
            if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new ModuleMigrationExecutionException("Un paquete del módulo falta o usa un enlace de archivo.");
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxPackageBytes)
                throw new ModuleMigrationExecutionException("Un paquete del módulo está vacío o supera 512 MiB.");

            await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var verified = ModulePackageVerifier.Verify(input, trustKeyRing);
            if (!string.IsNullOrWhiteSpace(verified.Module.Entitlement) && !grantedEntitlements.Contains(verified.Module.Entitlement))
                throw new ModuleMigrationExecutionException($"El módulo '{verified.Module.Id}' requiere una licencia activa para '{verified.Module.Entitlement}'.");
            if (!seenIds.Add(verified.Module.Id))
                throw new ModuleMigrationExecutionException($"El conjunto repite el módulo '{verified.Module.Id}'.");
            packages.Add(await PrepareModuleAsync(input, verified, cancellationToken));
        }

        IReadOnlyList<ModuleManifest> resolved;
        try { resolved = ModuleDependencyResolver.Resolve(packages.Select(item => item.Manifest).ToArray(), [requestedModuleId], hostApiVersion); }
        catch (ModuleResolutionException exception) { throw new ModuleMigrationExecutionException($"El conjunto de paquetes no es compatible: {exception.Message}", exception); }
        if (!resolved.Select(module => module.Id).SequenceEqual(packages.Select(package => package.Manifest.Id), StringComparer.Ordinal))
            throw new ModuleMigrationExecutionException("Los paquetes deben venir exactamente en orden de dependencias y sin módulos adicionales.");

        var migrations = packages.SelectMany(package => package.Migrations).ToArray();
        if (migrations.Length == 0) return [];

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({AdvisoryLockKey});", cancellationToken);
            await ExecuteAsync(connection, transaction, "CREATE SCHEMA IF NOT EXISTS \"pos_modules\";", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "CREATE TABLE IF NOT EXISTS \"pos_modules\".\"module_migration_history\" (" +
                "\"module_id\" text NOT NULL, \"migration_id\" text NOT NULL, \"sha256\" char(64) NOT NULL, " +
                "\"applied_at_utc\" timestamptz NOT NULL, PRIMARY KEY (\"module_id\", \"migration_id\"));", cancellationToken);

            var results = new List<ModuleMigrationExecutionResult>(migrations.Length);
            foreach (var package in packages)
            {
                var moduleSchema = ModuleMigrationSqlGenerator.SchemaForModule(package.Manifest.Id);
                if (package.Migrations.Count > 0)
                    await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS \"{moduleSchema}\";", cancellationToken);

                foreach (var migration in package.Migrations)
                {
                    var existingHash = await ReadAppliedHashAsync(connection, transaction, package.Manifest.Id, migration.Id, cancellationToken);
                    if (existingHash is not null)
                    {
                        if (!string.Equals(existingHash.Trim(), migration.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new ModuleMigrationExecutionException($"La migración '{migration.Id}' ya existe con otro checksum; se canceló toda la transacción.");
                        results.Add(new ModuleMigrationExecutionResult(package.Manifest.Id, migration.Id, migration.Sha256, AlreadyApplied: true));
                        continue;
                    }

                    foreach (var operation in migration.Definition.Operations)
                        await ExecuteAsync(connection, transaction, ModuleMigrationSqlGenerator.Generate(moduleSchema, operation), cancellationToken);
                    await InsertAppliedMigrationAsync(connection, transaction, package.Manifest.Id, migration.Id, migration.Sha256, cancellationToken);
                    results.Add(new ModuleMigrationExecutionResult(package.Manifest.Id, migration.Id, migration.Sha256, AlreadyApplied: false));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return results;
        }
        catch (ModuleMigrationExecutionException) { throw; }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ModuleMigrationExecutionException("No se pudo aplicar el conjunto de migraciones; PostgreSQL revirtió la transacción.", exception);
        }
    }

    private static async Task<PreparedModule> PrepareModuleAsync(Stream stream, VerifiedModulePackage verified, CancellationToken cancellationToken)
    {
        var manifest = verified.Module;
        var expectedMigrationPaths = manifest.Migrations.Select(MigrationPath).ToHashSet(StringComparer.Ordinal);
        var actualMigrationPaths = verified.Files.Select(file => file.Path)
            .Where(file => file.StartsWith("migrations/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!actualMigrationPaths.SetEquals(expectedMigrationPaths))
            throw new ModuleMigrationExecutionException($"Los archivos de migración de '{manifest.Id}' no coinciden con los IDs declarados en el manifiesto.");

        var migrations = new List<PreparedMigration>(manifest.Migrations.Count);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var migrationId in manifest.Migrations)
        {
            if (!migrationId.StartsWith(manifest.Id + ":", StringComparison.Ordinal))
                throw new ModuleMigrationExecutionException($"La migración '{migrationId}' no pertenece al módulo '{manifest.Id}'.");
            var migrationPath = MigrationPath(migrationId);
            var entry = archive.GetEntry(migrationPath) ?? throw new ModuleMigrationExecutionException($"Falta el archivo '{migrationPath}'.");
            if (entry.Length is <= 0 or > MaxMigrationBytes)
                throw new ModuleMigrationExecutionException($"La migración '{migrationId}' está vacía o supera 1 MiB.");
            await using var entryStream = entry.Open();
            using var contents = new MemoryStream((int)entry.Length);
            await entryStream.CopyToAsync(contents, cancellationToken);
            var bytes = contents.ToArray();
            if (bytes.Length != entry.Length || bytes.Length > MaxMigrationBytes)
                throw new ModuleMigrationExecutionException($"La migración '{migrationId}' excedió su tamaño declarado.");
            string json;
            try { json = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException exception) { throw new ModuleMigrationExecutionException($"La migración '{migrationId}' no usa UTF-8 válido.", exception); }
            ModuleMigrationDefinition definition;
            try { definition = ModuleMigrationReader.Parse(json); }
            catch (ModuleMigrationException exception) { throw new ModuleMigrationExecutionException($"La migración '{migrationId}' no es válida: {exception.Message}", exception); }
            migrations.Add(new PreparedMigration(migrationId, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), definition));
        }
        return new PreparedModule(manifest, migrations);
    }

    private static string MigrationPath(string migrationId) => $"migrations/{migrationId.Replace(':', '-')}.json";

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadAppliedHashAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string moduleId, string migrationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT \"sha256\" FROM \"pos_modules\".\"module_migration_history\" WHERE \"module_id\" = @module AND \"migration_id\" = @migration FOR UPDATE;",
            connection, transaction);
        command.Parameters.AddWithValue("module", moduleId);
        command.Parameters.AddWithValue("migration", migrationId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task InsertAppliedMigrationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string moduleId, string migrationId, string sha256, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO \"pos_modules\".\"module_migration_history\" (\"module_id\", \"migration_id\", \"sha256\", \"applied_at_utc\") " +
            "VALUES (@module, @migration, @sha256, @appliedAt);", connection, transaction);
        command.Parameters.AddWithValue("module", moduleId);
        command.Parameters.AddWithValue("migration", migrationId);
        command.Parameters.AddWithValue("sha256", sha256);
        command.Parameters.AddWithValue("appliedAt", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PreparedModule(ModuleManifest Manifest, IReadOnlyList<PreparedMigration> Migrations);
    private sealed record PreparedMigration(string Id, string Sha256, ModuleMigrationDefinition Definition);
}
