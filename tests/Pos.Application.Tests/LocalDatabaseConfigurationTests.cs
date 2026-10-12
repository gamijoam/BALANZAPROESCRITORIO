using System.Text;
using Pos.Desktop;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>
/// Criterios de aceptación de la configuración local:
/// - valida los datos antes de iniciar el servicio;
/// - persiste la conexión solo mediante el protector del sistema;
/// - puede recuperar la configuración cifrada para futuros inicios.
/// </summary>
public sealed class LocalDatabaseConfigurationTests
{
    [Fact]
    public void Connection_string_builder_preserves_special_characters_in_password()
    {
        var settings = new LocalDatabaseConfiguration("localhost", 5432, "pos_dev", "pos_app", "p;ass word");

        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = settings.ToConnectionString() };

        Assert.Equal("localhost", builder["Host"]);
        Assert.Equal("p;ass word", builder["Password"]);
    }

    [Fact]
    public void Store_round_trips_configuration_and_does_not_write_plaintext_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-config-{Guid.NewGuid():N}.bin");
        var protector = new TestSecretProtector();
        var store = new LocalDatabaseConfigurationStore(path, protector);
        var settings = new LocalDatabaseConfiguration("localhost", 5432, "pos_dev", "pos_app", "secret-test-value");

        try
        {
            store.Save(settings);
            var storedBytes = File.ReadAllBytes(path);
            var storedContent = Encoding.UTF8.GetString(storedBytes);
            var loaded = store.Load();

            Assert.DoesNotContain("secret-test-value", storedContent, StringComparison.Ordinal);
            Assert.Equal(settings, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Store_returns_null_when_no_local_configuration_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-config-missing-{Guid.NewGuid():N}.bin");

        var store = new LocalDatabaseConfigurationStore(path, new TestSecretProtector());

        Assert.Null(store.Load());
    }

    [Fact]
    public void Existing_database_setup_repairs_terminal_read_permissions_without_requiring_an_app_password_reset()
    {
        var root = FindRepositoryRoot();
        var configurator = File.ReadAllText(Path.Combine(root, "scripts", "Configure-LocalDatabase.ps1"));

        Assert.Contains("paired_terminals", configurator, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON TABLE public.$tableName TO", configurator, StringComparison.Ordinal);
        Assert.Contains("-U postgres -d $DatabaseName", configurator, StringComparison.Ordinal);
        Assert.Contains("SELECT 1 FROM public.$tableName LIMIT 0;", configurator, StringComparison.Ordinal);
        Assert.Contains("reuseApplicationPassword", configurator, StringComparison.Ordinal);

        var grantIndex = configurator.IndexOf("GRANT SELECT ON TABLE public.$tableName TO", StringComparison.Ordinal);
        var appConnectionValidationIndex = configurator.LastIndexOf("-U $DatabaseRole -d $DatabaseName -c 'SELECT 1;'", StringComparison.Ordinal);
        Assert.True(grantIndex >= 0 && appConnectionValidationIndex > grantIndex,
            "Los permisos existentes deben repararse con el usuario postgres antes de validar la conexión de pos_app.");
    }

    [Fact]
    public void Windows_protector_round_trips_a_secret_for_the_current_user()
    {
        var protector = new WindowsDpapiSecretProtector();
        var secret = Encoding.UTF8.GetBytes("pos-local-secret");

        var protectedData = protector.Protect(secret);
        var recovered = protector.Unprotect(protectedData);

        Assert.NotEqual(secret, protectedData);
        Assert.Equal(secret, recovered);
    }

    private sealed class TestSecretProtector : ILocalSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));
        public byte[] Unprotect(byte[] protectedData) => Convert.FromBase64String(Encoding.UTF8.GetString(protectedData));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
