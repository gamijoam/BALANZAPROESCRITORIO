namespace Pos.Application.Tests;

public sealed class WindowsServiceDeploymentAcceptanceTests
{
    [Fact]
    public void Server_is_configured_for_windows_service_hosting_and_protected_bootstrap()
    {
        var root = FindRepositoryRoot();
        var serverProject = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Pos.Server.csproj"));
        var program = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs"));
        var serviceConfiguration = Path.Combine(root, "src", "Pos.Server", "PosWindowsServiceConfiguration.cs");

        Assert.Contains("Microsoft.Extensions.Hosting.WindowsServices", serverProject, StringComparison.Ordinal);
        Assert.True(File.Exists(serviceConfiguration));
        Assert.Contains("UseWindowsService", program, StringComparison.Ordinal);
        Assert.Contains("PosWindowsServiceConfiguration.Apply", program, StringComparison.Ordinal);
        Assert.Contains("hostArguments", program, StringComparison.Ordinal);
        Assert.Contains("--windows-service", program, StringComparison.Ordinal);
        Assert.Contains("CryptUnprotectData", File.ReadAllText(serviceConfiguration), StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_is_self_contained_and_carries_the_service_lifecycle_tool()
    {
        var root = FindRepositoryRoot();
        var publisher = File.ReadAllText(Path.Combine(root, "scripts", "Publish-LocalPos.ps1"));
        var serviceTool = Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1");

        Assert.Contains("--runtime win-x64 --self-contained true", publisher, StringComparison.Ordinal);
        Assert.True(File.Exists(serviceTool));
        Assert.Contains("Manage-PosWindowsService.ps1", publisher, StringComparison.Ordinal);
        Assert.Contains("Pos.Server.exe", File.ReadAllText(serviceTool), StringComparison.Ordinal);
        Assert.Contains("BalanzaProPosServer", File.ReadAllText(serviceTool), StringComparison.Ordinal);
        Assert.Contains("Write-WindowsCommandFile", publisher, StringComparison.Ordinal);
        Assert.Contains("`r`n", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_update_stops_service_before_file_swap_and_restarts_new_version()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "Install-LocalPos.ps1"));
        var stop = installer.IndexOf("Stop-LocalPosServer", StringComparison.Ordinal);
        var swap = installer.IndexOf("Sync-PosInstallationFiles -Source $stage", StringComparison.Ordinal);
        var start = installer.LastIndexOf("Start-Service", StringComparison.Ordinal);

        Assert.Contains("Stop-Service", installer, StringComparison.Ordinal);
        Assert.Contains("Start-Service", installer, StringComparison.Ordinal);
        Assert.True(stop >= 0 && swap > stop && start > swap,
            "El servicio debe detenerse antes del reemplazo y arrancar después con los archivos nuevos.");
        Assert.Contains("registeredServerPath", installer, StringComparison.Ordinal);
        Assert.Contains("expectedServerPath", installer, StringComparison.Ordinal);
        Assert.Contains("robocopy.exe", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-Mirror", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Move-Item -LiteralPath $target -Destination $previous", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Installation_no_longer_requires_a_machine_wide_dotnet_runtime()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "Install-LocalPos.ps1"));
        var guide = File.ReadAllText(Path.Combine(root, "docs", "manuales", "INSTRUCCIONES-INSTALACION-POS.md"));

        Assert.DoesNotContain("--list-runtimes", installer, StringComparison.Ordinal);
        Assert.Contains("autocontenido", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("servicio de Windows", guide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_manager_detects_stale_executable_and_refuses_to_start_a_different_installation()
    {
        var root = FindRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1"));

        Assert.Contains("Test-PosServiceBinaryStale", manager, StringComparison.Ordinal);
        Assert.Contains("CreationDate", manager, StringComparison.Ordinal);
        Assert.Contains("No lo iniciaré desde este paquete", manager, StringComparison.Ordinal);
        Assert.Contains("ProtectMachine", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Database_backup_and_restore_hashes_do_not_depend_on_powershell_module_autoloading()
    {
        var root = FindRepositoryRoot();
        var databaseSupport = File.ReadAllText(Path.Combine(root, "scripts", "Pos-DatabaseSupport.ps1"));
        var backup = File.ReadAllText(Path.Combine(root, "scripts", "Backup-LocalDatabase.ps1"));
        var restore = File.ReadAllText(Path.Combine(root, "scripts", "Restore-LocalDatabase.ps1"));

        Assert.Contains("function Get-PosFileSha256", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("ComputeHash", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("Get-PosFileSha256", backup, StringComparison.Ordinal);
        Assert.Contains("Get-PosFileSha256", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_skips_protected_process_inspection_when_the_matching_server_is_a_windows_service()
    {
        var root = FindRepositoryRoot();
        var desktopHost = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "LocalPosServerProcessHost.cs"));
        var methodStart = desktopHost.IndexOf("public async Task EnsureCurrentServerAsync", StringComparison.Ordinal);
        var serviceCheck = desktopHost.IndexOf("IsInstalledServiceForThisServer()", methodStart, StringComparison.Ordinal);
        var processInspection = desktopHost.IndexOf("Process.GetProcessesByName", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && serviceCheck > methodStart && processInspection > serviceCheck,
            "Un servicio BalanzaPro con la ruta registrada no debe inspeccionarse mediante MainModule, porque puede ejecutarse como LocalSystem.");
    }

    [Fact]
    public void Service_manager_only_registers_the_permanent_installation()
    {
        var root = FindRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1"));

        Assert.Contains("$permanentInstallRoot", manager, StringComparison.Ordinal);
        Assert.Contains("No instales el servicio desde el ZIP extraído", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_manager_configures_binary_path_without_splitting_arguments_on_spaces()
    {
        var root = FindRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1"));

        Assert.Contains("Invoke-CimMethod", manager, StringComparison.Ordinal);
        Assert.Contains("MethodName Change", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-ServiceControl -Arguments @('config'", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-ServiceControl -Arguments @('create'", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_creation_uses_byte_values_required_by_the_Win32_Service_CIM_contract()
    {
        var root = FindRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1"));
        var createStart = manager.IndexOf("MethodName Create", StringComparison.Ordinal);
        var createEnd = manager.IndexOf("} else {", createStart, StringComparison.Ordinal);
        Assert.True(createStart >= 0 && createEnd > createStart, "No se encontró la llamada que registra el servicio Windows.");
        var createArguments = manager[createStart..createEnd];

        Assert.Contains("ServiceType = [byte]16", createArguments, StringComparison.Ordinal);
        Assert.Contains("ErrorControl = [byte]1", createArguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_configuration_can_replace_an_existing_machine_protected_file()
    {
        var root = FindRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1"));

        Assert.Contains("if (Test-Path -LiteralPath $configPath -PathType Leaf)", manager, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Replace($temporaryPath, $configPath, [NullString]::Value)", manager, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Move($temporaryPath, $configPath)", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("Move-Item -LiteralPath $temporaryPath -Destination $configPath", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Installed_updater_lets_the_user_select_a_new_extracted_package()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "Install-LocalPos.ps1"));

        Assert.Contains("FolderBrowserDialog", installer, StringComparison.Ordinal);
        Assert.Contains("Selecciona la carpeta donde extrajiste el ZIP nuevo", installer, StringComparison.Ordinal);
        Assert.Contains("scripts\\Install-LocalPos.ps1", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Ejecuta el instalador desde el paquete extraído, no desde la carpeta ya instalada.", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Lan_address_repair_reconfigures_the_installed_windows_service_before_probing()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));
        var manager = File.ReadAllText(Path.Combine(root, "scripts", "Manage-PosWindowsService.ps1"));

        Assert.Contains("Reconfigure", app, StringComparison.Ordinal);
        Assert.Contains("Reconfigure", manager, StringComparison.Ordinal);
        Assert.Contains("Set-PosServiceConfiguration", manager, StringComparison.Ordinal);
        Assert.Contains("Stop-Service", manager, StringComparison.Ordinal);
        Assert.Contains("Start-Service", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_startup_timeout_is_reported_as_a_pos_diagnostic_instead_of_a_canceled_task()
    {
        var root = FindRepositoryRoot();
        var desktopHost = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "LocalPosServerProcessHost.cs"));

        Assert.Contains("El servicio Windows no respondió", desktopHost, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException)", desktopHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_module_signing_script_uses_local_private_key_and_verifies_the_package()
    {
        var root = FindRepositoryRoot();
        var signingTool = File.ReadAllText(Path.Combine(root, "scripts", "Sign-PosModule.ps1"));
        var launcher = File.ReadAllText(Path.Combine(root, "scripts", "Sign-PosModule.cmd"));
        var guide = File.ReadAllText(Path.Combine(root, "docs", "security", "publicar-modulos.md"));

        Assert.Contains("Cert:\\CurrentUser\\My", signingTool, StringComparison.Ordinal);
        Assert.Contains("HasPrivateKey", signingTool, StringComparison.Ordinal);
        Assert.Contains("package-files.json", signingTool, StringComparison.Ordinal);
        Assert.Contains("--key-id", signingTool, StringComparison.Ordinal);
        Assert.Contains("export-public-key", signingTool, StringComparison.Ordinal);
        Assert.Contains("Test-SignedModulePackage.ps1", signingTool, StringComparison.Ordinal);
        Assert.DoesNotContain("Export-PfxCertificate", signingTool, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportRSAPrivateKey", signingTool, StringComparison.Ordinal);
        Assert.Contains("Sign-PosModule.ps1", launcher, StringComparison.Ordinal);
        Assert.Contains("Sign-PosModule.cmd", guide, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
