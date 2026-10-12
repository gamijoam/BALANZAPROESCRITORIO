using Npgsql;

namespace Pos.Infrastructure.Modules;

public sealed record InstalledModuleRecord(string ModuleId, string Version, string SignerKeyId, string Status,
    string BackupSha256, DateTime InstalledAtUtc, string? ActivationDetail = null);

/// <summary>Persists installed module versions and their host restart state in the server-owned schema.</summary>
public sealed class ModuleInstallationRegistry
{
    public async Task RecordRestartPendingAsync(string connectionString, ModuleInstallationResult result, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS \"pos_modules\";", connection, transaction))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using (var table = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS \"pos_modules\".\"installed_modules\" (\"module_id\" text PRIMARY KEY, \"version\" text NOT NULL, \"signer_key_id\" text NOT NULL, \"status\" text NOT NULL, \"backup_sha256\" char(64) NOT NULL, \"installed_at_utc\" timestamptz NOT NULL, \"activation_detail\" text NULL);", connection, transaction))
            await table.ExecuteNonQueryAsync(cancellationToken);
        await using (var column = new NpgsqlCommand("ALTER TABLE \"pos_modules\".\"installed_modules\" ADD COLUMN IF NOT EXISTS \"activation_detail\" text NULL;", connection, transaction))
            await column.ExecuteNonQueryAsync(cancellationToken);

        foreach (var package in result.Packages)
        {
            var planned = package;
            var signerKeyId = result.Plan.InstallationOrder.Single(entry => string.Equals(entry.Id, package.ModuleId, StringComparison.Ordinal)).SignerKeyId;
            await using var command = new NpgsqlCommand(
                "INSERT INTO \"pos_modules\".\"installed_modules\" (\"module_id\", \"version\", \"signer_key_id\", \"status\", \"backup_sha256\", \"installed_at_utc\", \"activation_detail\") VALUES (@id, @version, @signer, 'RestartPending', @backup, @installed, NULL) ON CONFLICT (\"module_id\") DO UPDATE SET \"version\" = EXCLUDED.\"version\", \"signer_key_id\" = EXCLUDED.\"signer_key_id\", \"status\" = 'RestartPending', \"backup_sha256\" = EXCLUDED.\"backup_sha256\", \"installed_at_utc\" = EXCLUDED.\"installed_at_utc\", \"activation_detail\" = NULL;", connection, transaction);
            command.Parameters.AddWithValue("id", planned.ModuleId);
            command.Parameters.AddWithValue("version", planned.Version.ToString(3));
            command.Parameters.AddWithValue("signer", signerKeyId);
            command.Parameters.AddWithValue("backup", result.Backup.Sha256);
            command.Parameters.AddWithValue("installed", result.Backup.CreatedAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InstalledModuleRecord>> GetAllAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT to_regclass('pos_modules.installed_modules') IS NOT NULL;", connection);
        if (await exists.ExecuteScalarAsync(cancellationToken) is not true) return [];
        await using (var column = new NpgsqlCommand("ALTER TABLE \"pos_modules\".\"installed_modules\" ADD COLUMN IF NOT EXISTS \"activation_detail\" text NULL;", connection))
            await column.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT \"module_id\", \"version\", \"signer_key_id\", \"status\", trim(\"backup_sha256\"), \"installed_at_utc\", \"activation_detail\" FROM \"pos_modules\".\"installed_modules\" ORDER BY \"module_id\";", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<InstalledModuleRecord>();
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new InstalledModuleRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return records;
    }

    public async Task SetActivationStatusAsync(string connectionString, string moduleId, string status, string? detail,
        CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var column = new NpgsqlCommand("ALTER TABLE \"pos_modules\".\"installed_modules\" ADD COLUMN IF NOT EXISTS \"activation_detail\" text NULL;", connection))
            await column.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE \"pos_modules\".\"installed_modules\" SET \"status\" = @status, \"activation_detail\" = @detail WHERE \"module_id\" = @id;", connection);
        command.Parameters.AddWithValue("id", moduleId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"No existe el módulo '{moduleId}' en el registro de instalaciones.");
    }

    public async Task<bool> SetDisabledToRestartPendingAsync(string connectionString, string moduleId,
        CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE \"pos_modules\".\"installed_modules\" SET \"status\" = 'RestartPending', \"activation_detail\" = NULL WHERE \"module_id\" = @id AND \"status\" = 'Disabled';", connection);
        command.Parameters.AddWithValue("id", moduleId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
