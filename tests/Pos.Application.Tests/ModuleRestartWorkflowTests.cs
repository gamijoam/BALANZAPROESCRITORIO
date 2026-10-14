using System.IO;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class ModuleRestartWorkflowTests
{
    [Theory]
    [InlineData("C:\\POS\\server\\Pos.Server.exe", "C:\\POS\\server\\Pos.Server.exe", "2026-10-12T09:00:00Z", "2026-10-12T09:01:00Z", false)]
    [InlineData("C:\\POS\\new\\Pos.Server.exe", "C:\\POS\\old\\Pos.Server.exe", "2026-10-12T09:00:00Z", "2026-10-12T09:01:00Z", true)]
    [InlineData("C:\\POS\\server\\Pos.Server.exe", "C:\\POS\\server\\Pos.Server.exe", "2026-10-12T09:02:00Z", "2026-10-12T09:01:00Z", true)]
    public void Restart_policy_detects_old_server_path_and_replaced_binary(string expectedPath, string runningPath,
        string executableUpdatedAt, string processStartedAt, bool expected)
    {
        var result = PosServerRestartPolicy.RequiresRestart(expectedPath, runningPath,
            DateTime.Parse(executableUpdatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(processStartedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Startup_replaces_a_stale_local_server_before_accepting_its_health_check()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));
        var host = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "LocalPosServerProcessHost.cs"));

        Assert.Contains("EnsureCurrentServerAsync", app, StringComparison.Ordinal);
        Assert.Contains("RestartAsync", host, StringComparison.Ordinal);
        Assert.Contains("File.GetLastWriteTimeUtc", host, StringComparison.Ordinal);
        Assert.Contains("MainModule?.FileName", host, StringComparison.Ordinal);
        Assert.Contains("Pos.Server.exe", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Modules_screen_offers_verified_local_restart_and_refreshes_loaded_desktop_modules()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "ManagementWindow.xaml"));
        var management = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "ManagementWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "MainWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));

        Assert.Contains("RestartModuleHostButton", xaml, StringComparison.Ordinal);
        Assert.Contains("RestartModuleHost_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("_restartLocalServiceAsync", management, StringComparison.Ordinal);
        Assert.Contains("RestartAsync", app, StringComparison.Ordinal);
        Assert.Contains("ReloadDesktopModulesAsync", main, StringComparison.Ordinal);
        Assert.Contains("ClientTerminal", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Modules_screen_offers_reactivation_for_disabled_modules_and_requires_restart()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "ManagementWindow.xaml"));
        var management = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "ManagementWindow.xaml.cs"));

        Assert.Contains("ActivateModuleButton", xaml, StringComparison.Ordinal);
        Assert.Contains("ActivateModule_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("Status: \"Disabled\"", management, StringComparison.Ordinal);
        Assert.Contains("ActivateModuleAsync", management, StringComparison.Ordinal);
        Assert.Contains("restart", management, StringComparison.OrdinalIgnoreCase);
        var activationHandler = management[management.IndexOf("private async void ActivateModule_Click", StringComparison.Ordinal)..
            management.IndexOf("private async void DeactivateModule_Click", StringComparison.Ordinal)];
        Assert.Contains("catch (Exception exception)", activationHandler, StringComparison.Ordinal);
        Assert.Contains("if (await LoadInstalledModulesAsync()) ModuleStatus.Text = error;", activationHandler, StringComparison.Ordinal);
        Assert.Contains("module_reactivation_failed", File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Reactivation_route_does_not_hold_an_operation_lease_while_waiting_for_exclusive_maintenance()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs"));
        var gateStart = program.IndexOf("if (context.Request.Method != HttpMethods.Get", StringComparison.Ordinal);
        Assert.True(gateStart >= 0, "No se encontró el middleware que protege las operaciones POS.");
        var gateEnd = program.IndexOf("await using var operation = await moduleMaintenanceGate.EnterOperationAsync", gateStart, StringComparison.Ordinal);
        Assert.True(gateEnd > gateStart, "No se encontró la adquisición del bloqueo de operación POS.");
        var gateCondition = program[gateStart..gateEnd];

        Assert.Contains("(?:activate|deactivate)$", gateCondition, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
