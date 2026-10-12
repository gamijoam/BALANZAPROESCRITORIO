using System.Data.Common;
using System.IO;
using System.Text.Json;

namespace Pos.Desktop.Presentation;

public sealed record LocalDatabaseConfiguration(string Host, int Port, string Database, string Username, string Password)
{
    public string ToConnectionString()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Indica el servidor PostgreSQL.", nameof(Host));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "El puerto debe estar entre 1 y 65535.");
        if (string.IsNullOrWhiteSpace(Database)) throw new ArgumentException("Indica la base de datos.", nameof(Database));
        if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("Indica el usuario PostgreSQL.", nameof(Username));
        if (string.IsNullOrWhiteSpace(Password)) throw new ArgumentException("Indica la contraseña PostgreSQL.", nameof(Password));

        var builder = new DbConnectionStringBuilder
        {
            ["Host"] = Host.Trim(),
            ["Port"] = Port,
            ["Database"] = Database.Trim(),
            ["Username"] = Username.Trim(),
            ["Password"] = Password,
            ["Timeout"] = 15,
            ["Command Timeout"] = 30
        };
        return builder.ConnectionString;
    }
}

public interface ILocalSecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Stores per-user connection settings only after protecting them with the platform secret protector.</summary>
public sealed class LocalDatabaseConfigurationStore(string path, ILocalSecretProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public LocalDatabaseConfiguration? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plaintext = protector.Unprotect(encrypted);
            try
            {
                var configuration = JsonSerializer.Deserialize<LocalDatabaseConfiguration>(plaintext, JsonOptions);
                if (configuration is null) throw new InvalidDataException();
                _ = configuration.ToConnectionString();
                return configuration;
            }
            finally
            {
                Array.Clear(plaintext);
            }
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidOperationException("No se pudo leer la configuración local protegida. Vuelve a configurar la conexión PostgreSQL.", exception);
        }
    }

    public void Save(LocalDatabaseConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = configuration.ToConnectionString();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions);
        byte[] protectedData;
        try { protectedData = protector.Protect(plaintext); }
        finally { Array.Clear(plaintext); }

        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("La ruta de configuración no tiene una carpeta válida.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedData);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            Array.Clear(protectedData);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
