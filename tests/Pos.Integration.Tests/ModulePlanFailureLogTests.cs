using Pos.Server;

namespace Pos.Integration.Tests;

public sealed class ModulePlanFailureLogTests
{
    [Fact]
    public void Writes_module_plan_failure_with_diagnostic_id_and_redacts_connection_string()
    {
        var directory = Directory.CreateTempSubdirectory("pos-module-diagnostics-");
        const string connectionString = "Host=localhost;Username=pos_app;Password=secret-value";
        try
        {
            var path = ModulePlanFailureLog.Write("abc123", "warehouses",
                new InvalidOperationException($"Connection failed: {connectionString}"), connectionString, directory.FullName);

            var contents = File.ReadAllText(path);
            Assert.Contains("abc123", contents);
            Assert.Contains("warehouses", contents);
            Assert.Contains("InvalidOperationException", contents);
            Assert.DoesNotContain("secret-value", contents);
            Assert.Contains("[conexión oculta]", contents);
        }
        finally { directory.Delete(recursive: true); }
    }
}
