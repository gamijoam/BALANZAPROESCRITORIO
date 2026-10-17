using System.Globalization;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class App : Application
{
    private PosApiClient? _apiClient;
    private LocalPosServerProcessHost? _localServer;
    private bool _canRestartLocalService;
    private static Mutex? _singleInstanceMutex;
    private static bool _ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = PosNumberFormat.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = PosNumberFormat.Culture;
        Thread.CurrentThread.CurrentCulture = PosNumberFormat.Culture;
        Thread.CurrentThread.CurrentUICulture = PosNumberFormat.Culture;
        base.OnStartup(e);

        if (!TryOwnSingleInstanceMutex())
        {
            ActivateExistingInstance();
            Shutdown(0);
            return;
        }

        var startupSplash = new StartupSplashWindow();
        MainWindow = startupSplash;
        startupSplash.Show();
        // Let WPF paint the splash before any first-run dialogs or service startup work.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        startupSplash.SetStatus("Leyendo la configuración de este equipo…");

        var apiBaseUrl = Environment.GetEnvironmentVariable("POS_API_BASE_URL");
        var configurationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuntoDeVenta", "Configuracion");
        var profileStore = new PosInstallationProfileStore(Path.Combine(configurationDirectory, "deployment.dpapi"), new WindowsDpapiSecretProtector());
        PosInstallationProfile? profile;
        try { profile = profileStore.Load(); }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "Perfil de instalación no disponible", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (profile is null && string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            startupSplash.SetStatus("Configurando el punto de venta por primera vez…");
            var setup = new InstallationSetupWindow(configurationDirectory);
            if (setup.ShowDialog() != true || setup.Profile is null)
            {
                Shutdown(0);
                return;
            }
            profile = setup.Profile;
            profileStore.Save(profile);
        }

        var startLocalServer = false;
        if (profile is not null)
        {
            apiBaseUrl = profile.ApiBaseUrl;
            if (profile.Mode == PosInstallationMode.Standalone)
            {
                apiBaseUrl = "http://127.0.0.1:5155";
                startLocalServer = true;
            }
            else if (profile.Mode == PosInstallationMode.Server)
            {
                startLocalServer = true;
                try
                {
                    var listenAddress = new Uri(profile.ApiBaseUrl);
                    var localAddresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .Where(network => network.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                        .Select(address => address.Address);
                    var resolvedAddresses = System.Net.Dns.GetHostAddresses(listenAddress.Host);
                    if (LocalLanServerConfiguration.RequiresAddressRepair(resolvedAddresses, localAddresses))
                    {
                        var previousAddress = listenAddress.Host;
                        var currentAddress = LocalLanServerConfiguration.FindServerAddress();
                        var certificateFileName = $"servidor-lan-{Guid.NewGuid():N}.pfx";
                        var certificate = LocalLanServerConfiguration.CreateServerCertificate(currentAddress, configurationDirectory, certificateFileName);
                        var currentEndpoint = new UriBuilder(Uri.UriSchemeHttps, currentAddress.ToString(), LocalLanServerConfiguration.Port).Uri;
                        profile = profile with
                        {
                            ApiBaseUrl = currentEndpoint.ToString(),
                            ServerCertificatePath = certificate.CertificatePath,
                            ServerCertificatePassword = certificate.CertificatePassword,
                            ServerCertificateSha256 = certificate.CertificateSha256
                        };
                        profileStore.Save(profile);
                        apiBaseUrl = profile.ApiBaseUrl;
                        listenAddress = new Uri(profile.ApiBaseUrl);
                        var installedServerPath = ResolveServerPath();
                        if (LocalPosServerProcessHost.IsInstalledWindowsServiceForPath(installedServerPath))
                            await ReconfigureInstalledServiceForNetworkAsync();
                        MessageBox.Show(
                            $"La dirección guardada ({previousAddress}) ya no está asignada a esta computadora. El POS se reconfiguró para usar {currentAddress}:{LocalLanServerConfiguration.Port} y renovó el certificado HTTPS.\n\nActualiza la dirección y confirma el nuevo certificado en cada terminal cliente.",
                            "Se actualizó la dirección del servidor", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    if (listenAddress.AbsolutePath != "/" || !string.IsNullOrEmpty(listenAddress.Query))
                        throw new InvalidOperationException("La dirección HTTPS del servidor debe ser raíz, sin ruta ni parámetros.");
                }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(exception.Message, "Dirección de servidor no válida", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }
        }
        else
        {
            apiBaseUrl ??= "http://127.0.0.1:5155";
        }

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri))
        {
            MessageBox.Show("La dirección del servicio POS_API_BASE_URL o del perfil de instalación no es válida.", "Punto de venta", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (startLocalServer || (profile is null && apiUri.IsLoopback && apiUri.Port == 5155))
        {
            startupSplash.SetStatus("Iniciando y comprobando el servicio local…");
            if (profile?.Mode != PosInstallationMode.Server)
            {
                // Keep the single-PC API bound to IPv4 loopback only.
                apiBaseUrl = "http://127.0.0.1:5155";
                apiUri = new Uri(apiBaseUrl);
            }
            while (true)
            {
                try
                {
                    await EnsureLocalServerReadyAsync(apiUri, profile);
                    break;
                }
                catch (Exception exception)
                {
                    var result = MessageBox.Show(
                        $"{exception.Message}{Environment.NewLine}{Environment.NewLine}Cuando PostgreSQL esté iniciado o corregida la conexión, selecciona Sí para volver a comprobarla. Selecciona No para cerrar el POS.",
                        "Servicio local no disponible", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes) continue;
                    Shutdown(1);
                    return;
                }
            }

            if (profile is not null)
            {
                profile = profile with { DatabaseConfigured = true };
                profileStore.Save(profile);
            }
        }
        else if (profile?.Mode == PosInstallationMode.ClientTerminal)
        {
            startupSplash.SetStatus("Conectando con el servidor del punto de venta…");
            while (true)
            {
                try
                {
                    await EnsureRemoteServiceReadyAsync(apiUri, profile?.ServerCertificateSha256);
                    break;
                }
                catch (Exception exception)
                {
                    var result = MessageBox.Show(
                        $"{exception.Message}{Environment.NewLine}{Environment.NewLine}Sí: volver a intentar. No: cambiar la IP y confirmar de nuevo el servidor. Cancelar: cerrar el POS.",
                        "Servidor POS no disponible", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes) continue;
                    if (result == MessageBoxResult.No)
                    {
                        var setup = new InstallationSetupWindow(configurationDirectory, PosInstallationMode.ClientTerminal);
                        if (setup.ShowDialog() == true && setup.Profile is not null)
                        {
                            profile = setup.Profile;
                            profileStore.Save(profile);
                            apiBaseUrl = profile.ApiBaseUrl;
                            apiUri = new Uri(apiBaseUrl);
                            break;
                        }
                    }
                    Shutdown(1);
                    return;
                }
            }
            profileStore.Save(profile!);
        }

        startupSplash.SetStatus("Abriendo el punto de venta…");
        _apiClient = new PosApiClient(new Uri(apiBaseUrl), terminalCredential: profile?.TerminalCredential,
            serverCertificateSha256: profile?.ServerCertificateSha256);
        _apiClient.RequestAdministratorPinAsync = (challenge, _) =>
        {
            var dialog = new AdminPinApprovalWindow(challenge) { Owner = MainWindow };
            return Task.FromResult(dialog.ShowDialog() == true ? dialog.Credentials : null);
        };
        var viewModel = new PosViewModel(_apiClient);
        var window = new MainWindow(viewModel, _apiClient,
            startLocalServer && _canRestartLocalService ? RestartLocalServiceAndReloadModulesAsync : null,
            stopsLocalServerOnExit: startLocalServer && !LocalPosServerProcessHost.IsInstalledWindowsServiceForPath(ResolveServerPath()));
        MainWindow = window;
        await startupSplash.KeepVisibleForAnimationAsync();
        window.Show();
        startupSplash.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _apiClient?.Dispose();
        _localServer?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static bool TryOwnSingleInstanceMutex()
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var mutexName = $"Local\\BalanzaPro.Pos.Desktop.{identity}";
        _singleInstanceMutex = new Mutex(true, mutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (createdNew) return true;
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private static void ActivateExistingInstance()
    {
        foreach (var process in Process.GetProcessesByName("Pos.Desktop"))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero) continue;
                ShowWindow(window, 9);
                SetForegroundWindow(window);
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally { process.Dispose(); }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private async Task RestartLocalServiceAndReloadModulesAsync(CancellationToken cancellationToken)
    {
        if (_localServer is null)
            throw new InvalidOperationException("Esta terminal no administra el servidor. Reinicia el servicio desde la computadora servidor.");

        await _localServer.RestartAsync(cancellationToken);
        if (MainWindow is MainWindow mainWindow)
        {
            if (!await mainWindow.ReloadDesktopModulesAsync(cancellationToken))
                throw new InvalidOperationException("El servidor volvió a estar listo, pero no se pudo cargar una o más pantallas. Revisa el mensaje de estado del POS.");
        }
    }

    private async Task EnsureLocalServerReadyAsync(Uri apiBaseAddress, PosInstallationProfile? profile = null)
    {
        _localServer?.Dispose();
        _localServer = null;
        var localDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuntoDeVenta", "Configuracion");
        var store = new LocalDatabaseConfigurationStore(Path.Combine(localDataDirectory, "base-datos.dpapi"), new WindowsDpapiSecretProtector());
        LocalDatabaseConfiguration? settings;
        try
        {
            settings = store.Load();
        }
        catch (InvalidOperationException exception)
        {
            throw new PosServerStartupException(
                $"{exception.Message} El POS y el actualizador deben ejecutarse con la misma cuenta de Windows. Si necesitas regenerar la conexión, cierra el POS y ejecuta scripts\\Configure-LocalDatabase.ps1 desde la instalación con esa cuenta; el script pedirá la contraseña del administrador postgres y guardará una nueva clave para el usuario de aplicación. No escribas la contraseña postgres si esta pantalla indica que el usuario es pos_app.");
        }

        if (settings is null)
        {
            settings = TryReadLegacyUserSecrets();
            if (settings is not null) store.Save(settings);
        }

        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuntoDeVenta", "Logs");
        var serverPath = ResolveServerPath();
        var probeHost = CreateLocalServerHost(apiBaseAddress, serverPath, logs, settings, profile);
        await probeHost.EnsureCurrentServerAsync();
        var health = await probeHost.ProbeAsync();
        if (health == PosServerHealth.NotRunning && !File.Exists(serverPath))
        {
            probeHost.Dispose();
            throw new PosServerStartupException($"No se encontró el servidor local del POS junto a la aplicación. Revisa el paquete actualizado. Ruta esperada: {serverPath}");
        }
        if (health == PosServerHealth.NotRunning && settings is null)
        {
            var setupWindow = new DatabaseSetupWindow();
            if (setupWindow.ShowDialog() != true || setupWindow.Configuration is null)
            {
                probeHost.Dispose();
                throw new PosServerStartupException("La conexión PostgreSQL no se configuró. El POS no puede iniciar sesión sin el servicio local.");
            }
            settings = setupWindow.Configuration;
            store.Save(settings);
        }

        if (health != PosServerHealth.NotRunning && settings is null)
        {
            _localServer = probeHost;
        }
        else
        {
            _localServer = CreateLocalServerHost(apiBaseAddress, serverPath, logs, settings!, profile);
            probeHost.Dispose();
        }
        _canRestartLocalService = settings is not null;

        try
        {
            await new PosLocalStartupCoordinator(_localServer).EnsureReadyAsync();
        }
        catch (Exception exception) when (health == PosServerHealth.NotRunning &&
                                          exception is PosDatabaseUnavailableException or PosServerStartupException)
        {
            var choice = MessageBox.Show(
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}¿Quieres revisar o corregir los datos de PostgreSQL?",
                "No se pudo conectar con PostgreSQL", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) throw;

            var setupWindow = new DatabaseSetupWindow(settings);
            if (setupWindow.ShowDialog() != true || setupWindow.Configuration is null) throw;
            settings = setupWindow.Configuration;
            store.Save(settings);
            _localServer.Dispose();
            _localServer = CreateLocalServerHost(apiBaseAddress, serverPath, logs, settings, profile);
            await new PosLocalStartupCoordinator(_localServer).EnsureReadyAsync();
        }
    }

    private static async Task ReconfigureInstalledServiceForNetworkAsync()
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "scripts", "Manage-PosWindowsService.ps1"));
        if (!File.Exists(scriptPath))
            throw new PosServerStartupException($"La IP de esta computadora cambió y el servicio Windows debe actualizarse, pero no se encontró el administrador del servicio: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Action");
        startInfo.ArgumentList.Add("Reconfigure");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new PosServerStartupException("Windows no pudo abrir el administrador para actualizar el servicio POS.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new PosServerStartupException("No se pudo actualizar la dirección y el certificado del servicio Windows. Ejecuta 'Administrar servicio POS.cmd' como administrador y elige Instalar / actualizar configuración.");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new PosServerStartupException("Windows canceló el permiso de administrador. El servicio conserva la dirección anterior; acepta el aviso de Windows para actualizarlo y volver a iniciar el POS.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new PosServerStartupException($"Windows no pudo reconfigurar el servicio POS. Detalle: {exception.Message}");
        }
    }

    private static LocalPosServerProcessHost CreateLocalServerHost(Uri apiBaseAddress, string serverPath, string logs,
        LocalDatabaseConfiguration? database, PosInstallationProfile? profile)
    {
        var isLanServer = profile?.Mode == PosInstallationMode.Server;
        return new LocalPosServerProcessHost(apiBaseAddress, serverPath, logs, database?.ToConnectionString(),
            isLanServer ? apiBaseAddress.ToString().TrimEnd('/') : null,
            isLanServer ? profile!.ServerCertificatePath : null,
            isLanServer ? profile!.ServerCertificatePassword : null,
            isLanServer ? profile!.ServerCertificateSha256 : null);
    }

    private static async Task EnsureRemoteServiceReadyAsync(Uri apiBaseAddress, string? serverCertificateSha256 = null)
    {
        var handler = ServerCertificatePin.IsValid(serverCertificateSha256)
            ? ServerCertificatePin.CreatePinnedHandler(serverCertificateSha256!)
            : new HttpClientHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(new Uri(apiBaseAddress.ToString().TrimEnd('/') + "/health/ready"));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"El servicio respondió {((int)response.StatusCode)} y no está listo para recibir ventas.");
        using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (!document.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ready")
            throw new InvalidOperationException("El servicio HTTPS respondió, pero PostgreSQL no está listo.");
    }

    private static string ResolveServerPath()
    {
        var appDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(appDirectory, "..", "server", "Pos.Server.exe")),
            Path.GetFullPath(Path.Combine(appDirectory, "..", "net10.0", "Pos.Server.exe")),
            Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "..", "..", "Pos.Server", "bin", "Debug", "net10.0", "Pos.Server.exe")),
            Path.GetFullPath(Path.Combine(appDirectory, "..", "..", "..", "..", "Pos.Server", "bin", "Release", "net10.0", "Pos.Server.exe"))
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static LocalDatabaseConfiguration? TryReadLegacyUserSecrets()
    {
        const string userSecretsId = "f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
        if (!File.Exists(path)) return null;

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("ConnectionStrings", out var strings) ||
            !strings.TryGetProperty("Pos", out var value) || string.IsNullOrWhiteSpace(value.GetString())) return null;

        var builder = new DbConnectionStringBuilder { ConnectionString = value.GetString()! };
        string Read(string key) => builder.TryGetValue(key, out var result) ? Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        if (!int.TryParse(Read("Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) port = 5432;
        var configuration = new LocalDatabaseConfiguration(Read("Host"), port, Read("Database"), Read("Username"), Read("Password"));
        _ = configuration.ToConnectionString();
        return configuration;
    }
}
