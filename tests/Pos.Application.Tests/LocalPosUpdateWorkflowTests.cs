namespace Pos.Application.Tests;

public sealed class LocalPosUpdateWorkflowTests
{
    [Fact]
    public void Server_update_uses_the_saved_certificate_pin_when_verifying_the_listener_before_stopping_it()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "Install-LocalPos.ps1"));

        Assert.Contains("ServerCertificateSha256", installer, StringComparison.Ordinal);
        Assert.Contains("ServerCertificateValidationCallback", installer, StringComparison.Ordinal);
        Assert.Contains("-CertificateSha256 $certificateSha256", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Port_stopper_stops_a_windows_service_instead_of_killing_its_restartable_process()
    {
        var root = FindRepositoryRoot();
        var stopper = File.ReadAllText(Path.Combine(root, "scripts", "Stop-ProcessByPort.ps1"));
        var stopLauncher = File.ReadAllText(Path.Combine(root, "Detener proceso por puerto.cmd"));
        var publisher = File.ReadAllText(Path.Combine(root, "scripts", "Publish-LocalPos.ps1"));

        Assert.Contains("Win32_Service", stopper, StringComparison.Ordinal);
        Assert.Contains("Stop-Service", stopper, StringComparison.Ordinal);
        Assert.Contains("ProcessId", stopper, StringComparison.Ordinal);
        Assert.Contains("El puerto sigue ocupado", stopper, StringComparison.Ordinal);
        Assert.Contains("Pos.Desktop.exe", stopper, StringComparison.Ordinal);
        Assert.Contains("aunque no tenga ventana visible", stopper, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath '%~f0' -Verb RunAs", stopLauncher, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath '%~f0' -Verb RunAs", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_reports_the_process_id_and_path_blocking_the_update()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "Install-LocalPos.ps1"));

        Assert.Contains("Win32_Process", installer, StringComparison.Ordinal);
        Assert.Contains("Detener proceso por puerto.cmd", installer, StringComparison.Ordinal);
        Assert.Contains("PID", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_validates_backup_before_stopping_local_server_and_migrates_before_launching_desktop()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "Install-LocalPos.ps1"));
        var publisher = File.ReadAllText(Path.Combine(root, "scripts", "Publish-LocalPos.ps1"));
        var databaseSupport = File.ReadAllText(Path.Combine(root, "scripts", "Pos-DatabaseSupport.ps1"));
        var databaseConfigurator = File.ReadAllText(Path.Combine(root, "scripts", "Configure-LocalDatabase.ps1"));
        var desktopStartup = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "App.xaml.cs"));
        var serverProcessHost = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "LocalPosServerProcessHost.cs"));
        var databaseSetup = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "DatabaseSetupWindow.xaml"));
        var installGuide = File.ReadAllText(Path.Combine(root, "docs", "manuales", "INSTRUCCIONES-INSTALACION-POS.md"));

        Assert.Contains("Stop-LocalPosServer", installer, StringComparison.Ordinal);
        Assert.Contains("Backup-LocalDatabase.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("--migrate", installer, StringComparison.Ordinal);
        Assert.Contains("Start-Process", installer, StringComparison.Ordinal);
        Assert.Contains("Actualizar POS.cmd", publisher, StringComparison.Ordinal);
        Assert.Contains("Start-LocalPos.ps1", publisher, StringComparison.Ordinal);
        Assert.Contains("appsettings.Development.json", publisher, StringComparison.Ordinal);
        Assert.Contains("Remove-PublishedBuildArtifacts", publisher, StringComparison.Ordinal);
        Assert.Contains("[string]$ArchivePath", publisher, StringComparison.Ordinal);
        Assert.Contains("INSTRUCCIONES-INSTALACION-POS.md", publisher, StringComparison.Ordinal);
        Assert.Contains("INSTRUCCIONES.md", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("-Destination (Join-Path $packageDirectory 'MODOS DE INSTALACION.md')", publisher, StringComparison.Ordinal);
        Assert.Contains("-PackageDirectory \"%~dp0.\"", publisher, StringComparison.Ordinal);
        Assert.Contains("aunque no haya una ventana visible", installer, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection", installer, StringComparison.Ordinal);
        Assert.Contains("base-datos.dpapi", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("Get-PosInstallationProfile", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("CryptUnprotectData", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("2>&1", databaseSupport, StringComparison.Ordinal);
        Assert.DoesNotContain("=>", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("Escribe la IP del servidor", installGuide, StringComparison.Ordinal);
        Assert.Contains("confirma si corresponde a tu caja principal", installGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("pos-servidor.pfx", installGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("código de emparejamiento", installGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public static byte[] Protect(byte[] input) {", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("PosLocalDpapi]::Protect", databaseConfigurator, StringComparison.Ordinal);
        Assert.Contains("base-datos.dpapi", databaseConfigurator, StringComparison.Ordinal);
        Assert.Contains("reuseApplicationPassword", databaseConfigurator, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (InvalidOperationException)\n        {\n            settings = null;", desktopStartup, StringComparison.Ordinal);
        Assert.Contains("La contraseña debe pertenecer al usuario indicado arriba", databaseSetup, StringComparison.Ordinal);
        Assert.Contains("ClientTerminal", installer, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_Kestrel__Certificates__Default__Password", installer, StringComparison.Ordinal);
        Assert.Contains("Get-PostgreSqlBinaryDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("POS_POSTGRES_BIN", installer, StringComparison.Ordinal);
        Assert.Contains("Get-PostgreSqlBinaryDirectory", databaseSupport, StringComparison.Ordinal);
        Assert.Contains("GetPostgreSqlBinaryDirectories", serverProcessHost, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlBinaryDirectoryResolver.Resolve", serverProcessHost, StringComparison.Ordinal);

        var backupIndex = installer.IndexOf("& $backupScript", StringComparison.Ordinal);
        var stopIndex = System.Text.RegularExpressions.Regex.Match(installer, "(?m)^\\s+Stop-LocalPosServer\\s+-Port").Index;
        var migrationIndex = installer.IndexOf("--migrate", StringComparison.Ordinal);
        var launchIndex = installer.LastIndexOf("Start-Process", StringComparison.Ordinal);
        Assert.True(backupIndex < stopIndex && stopIndex < migrationIndex && migrationIndex < launchIndex,
            "El flujo debe validar el respaldo antes de detener el servidor, migrar y abrir la versión actualizada.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
