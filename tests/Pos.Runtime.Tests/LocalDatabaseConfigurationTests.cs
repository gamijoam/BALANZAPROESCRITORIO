using System.Data.Common;
using System.Text;
using Pos.Desktop.Presentation;

namespace Pos.Runtime.Tests;

/// <summary>Acceptance criteria for local database configuration.</summary>
public sealed class LocalDatabaseConfigurationTests
{
    [Fact]
    public void Connection_string_builder_preserves_special_characters_in_password()
    {
        var settings = new LocalDatabaseConfiguration("localhost", 5432, "pos_dev", "pos_app", "p;ass word");
        var builder = new DbConnectionStringBuilder { ConnectionString = settings.ToConnectionString() };

        Assert.Equal("localhost", builder["Host"]);
        Assert.Equal("p;ass word", builder["Password"]);
    }

    [Fact]
    public void Store_round_trips_configuration_and_does_not_write_plaintext_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-config-{Guid.NewGuid():N}.bin");
        var store = new LocalDatabaseConfigurationStore(path, new TestSecretProtector());
        var settings = new LocalDatabaseConfiguration("localhost", 5432, "pos_dev", "pos_app", "secret-test-value");

        try
        {
            store.Save(settings);
            var storedContent = Encoding.UTF8.GetString(File.ReadAllBytes(path));

            Assert.DoesNotContain("secret-test-value", storedContent, StringComparison.Ordinal);
            Assert.Equal(settings, store.Load());
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

    private sealed class TestSecretProtector : ILocalSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));
        public byte[] Unprotect(byte[] protectedData) => Convert.FromBase64String(Encoding.UTF8.GetString(protectedData));
    }
}
