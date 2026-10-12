using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

internal sealed class LocalPosServerProcessHost : IPosServerHost, IDisposable
{
    private readonly Uri _baseAddress;
    private readonly string? _connectionString;
    private readonly string? _listenUrls;
    private readonly string? _certificatePath;
    private readonly string? _certificatePassword;
    private readonly string _serverPath;
    private readonly string _logPath;
    private readonly HttpClient _httpClient;
    private readonly object _logLock = new();
    private Process? _process;

    public LocalPosServerProcessHost(Uri baseAddress, string serverPath, string logDirectory, string? connectionString,
        string? listenUrls = null, string? certificatePath = null, string? certificatePassword = null,
        string? serverCertificateSha256 = null)
    {
        _baseAddress = new Uri(baseAddress.ToString().TrimEnd('/') + "/");
        _serverPath = serverPath;
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "servidor-local.log");
        _connectionString = connectionString;
        _listenUrls = listenUrls;
        _certificatePath = certificatePath;
        _certificatePassword = certificatePassword;
        var handler = ServerCertificatePin.IsValid(serverCertificateSha256)
            ? ServerCertificatePin.CreatePinnedHandler(serverCertificateSha256!)
            : new HttpClientHandler();
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    public async Task<PosServerHealth> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: true })
            throw new PosServerStartupException($"El servicio local se cerró. Revisa el registro: {_logPath}");

        try
        {
            using var readyResponse = await _httpClient.GetAsync(new Uri(_baseAddress, "health/ready"), cancellationToken);
            if (readyResponse.IsSuccessStatusCode)
            {
                using var stream = await readyResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return document.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ready"
                    ? PosServerHealth.Ready
                    : PosServerHealth.Starting;
            }

            if (readyResponse.StatusCode == HttpStatusCode.ServiceUnavailable && await IsLiveAsync(cancellationToken))
                return PosServerHealth.DatabaseUnavailable;
            return PosServerHealth.Starting;
        }
        catch (HttpRequestException)
        {
            return await IsLiveAsync(cancellationToken) ? PosServerHealth.DatabaseUnavailable : PosServerHealth.NotRunning;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await IsLiveAsync(cancellationToken) ? PosServerHealth.DatabaseUnavailable : PosServerHealth.NotRunning;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false }) return;
        if (!File.Exists(_serverPath))
            throw new PosServerStartupException($"No se encontró el servidor local del POS: {_serverPath}");
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new PosServerStartupException("Falta la configuración local de PostgreSQL. Completa la configuración inicial del POS.");

        var migration = StartProcess("--migrate");
        using (migration)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await migration.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { migration.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new PosServerStartupException($"La preparación de PostgreSQL tardó más de 60 segundos. Revisa el registro: {_logPath}");
            }
            migration.WaitForExit();
            if (migration.ExitCode != 0)
                throw new PosServerStartupException($"No se pudo conectar a PostgreSQL o preparar las tablas del POS. Revisa que PostgreSQL esté iniciado y verifica el servidor, la base, el usuario y la contraseña. Registro: {_logPath}");
        }

        _process = StartProcess(string.Empty);
    }

    /// <summary>Stops a healthy older local host when the launcher points at a newer server package.</summary>
    public async Task EnsureCurrentServerAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_serverPath))
        {
            if (await IsLiveAsync(cancellationToken))
                throw new PosServerStartupException($"El servicio POS responde, pero no se encontró el servidor actualizado esperado: {_serverPath}");
            return;
        }

        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_serverPath));
        try
        {
            if (processes.Length == 0 || !await IsLiveAsync(cancellationToken)) return;
            if (processes.Length != 1)
                throw new PosServerStartupException("Hay varias instancias Pos.Server.exe y el servicio responde en el puerto configurado. No se detuvo ninguna para evitar interrumpir otra instalación.");

            var running = processes[0];
            string runningPath;
            DateTime startedAtUtc;
            try
            {
                runningPath = Path.GetFullPath(running.MainModule?.FileName
                    ?? throw new InvalidOperationException("Windows no informó la ruta del proceso."));
                startedAtUtc = running.StartTime.ToUniversalTime();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                throw new PosServerStartupException($"El servidor POS responde, pero no se pudo verificar qué ejecutable lo inició. No se detuvo. Detalle: {exception.Message}");
            }

            var expectedPath = Path.GetFullPath(_serverPath);
            var isOutdated = PosServerRestartPolicy.RequiresRestart(expectedPath, runningPath,
                File.GetLastWriteTimeUtc(expectedPath), startedAtUtc);
            if (isOutdated)
            {
                await StopProcessAsync(running, cancellationToken);
                WriteLog($"Se detuvo el servidor anterior '{runningPath}' para iniciar el ejecutable actual '{expectedPath}'.");
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>Restarts the verified local POS service using the server next to this desktop build.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_serverPath))
            throw new PosServerStartupException($"No se encontró el servidor local que debe reiniciarse: {_serverPath}");

        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_serverPath));
        try
        {
            if (processes.Length > 1)
                throw new PosServerStartupException("Hay varias instancias Pos.Server.exe. No se reinició ninguna porque no es seguro elegir cuál atiende este POS.");

            var endpointIsLive = await IsLiveAsync(cancellationToken);
            if (processes.Length == 1 && endpointIsLive)
                await StopProcessAsync(processes[0], cancellationToken);
            else if (endpointIsLive)
                throw new PosServerStartupException("El servidor responde, pero no se pudo identificar su proceso local. No se reinició un proceso desconocido.");
            else if (_process is { HasExited: false })
                await StopAsync(cancellationToken);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        await StartAsync(cancellationToken);
        await new PosLocalStartupCoordinator(this).EnsureReadyAsync(cancellationToken);
    }

    private async Task StopProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PosServerStartupException("El servidor POS no se detuvo en 15 segundos.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new PosServerStartupException($"Windows no permitió detener el servidor POS. Ejecuta el POS con la misma cuenta que inició el servidor. Detalle: {exception.Message}");
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Another local supervisor may have stopped the verified POS process concurrently.
        }
        if (_process is not null && _process.Id == process.Id)
        {
            _process.Dispose();
            _process = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task<bool> IsLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(_baseAddress, "health/live"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private Process StartProcess(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _serverPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(_serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["ConnectionStrings__Pos"] = _connectionString;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = _listenUrls ?? _baseAddress.ToString().TrimEnd('/');
        var postgresBins = GetPostgreSqlBinaryDirectories();
        var postgresBin = PostgreSqlBinaryDirectoryResolver.Resolve(
            Environment.GetEnvironmentVariable("POS_POSTGRES_BIN"), postgresBins,
            PostgreSqlBinaryDirectoryResolver.HasRequiredTools);
        if (postgresBin is null) startInfo.Environment.Remove("POS_POSTGRES_BIN");
        else startInfo.Environment["POS_POSTGRES_BIN"] = postgresBin;
        if (!string.IsNullOrWhiteSpace(_certificatePath))
            startInfo.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = _certificatePath;
        if (!string.IsNullOrWhiteSpace(_certificatePassword))
            startInfo.Environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = _certificatePassword;
        startInfo.Environment["PosSecurity__RequireTerminalPairing"] = "false";
        var localTrustKeyRing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PuntoDeVenta", "Security", "TrustedModuleKeys.json");
        if (File.Exists(localTrustKeyRing))
            startInfo.Environment["ModuleTrust__KeyRingPath"] = localTrustKeyRing;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => WriteLog(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => WriteLog(eventArgs.Data);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows no pudo iniciar el proceso del servidor.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new PosServerStartupException($"No se pudo iniciar el servicio local del POS. Revisa permisos y el archivo {_serverPath}. Detalle: {exception.Message}");
        }
    }

    private static IEnumerable<string> GetPostgreSqlBinaryDirectories()
    {
        var programFilesRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in programFilesRoots)
        {
            var postgresRoot = Path.Combine(root, "PostgreSQL");
            if (!Directory.Exists(postgresRoot)) continue;
            foreach (var versionDirectory in Directory.EnumerateDirectories(postgresRoot))
                yield return Path.Combine(versionDirectory, "bin");
        }
    }

    private void WriteLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var safeLine = _connectionString is { Length: > 0 } connectionString
            ? line.Replace(connectionString, "[conexión oculta]", StringComparison.Ordinal)
            : line;
        if (_certificatePassword is { Length: > 0 } certificatePassword)
            safeLine = safeLine.Replace(certificatePassword, "[contraseña de certificado oculta]", StringComparison.Ordinal);
        try
        {
            lock (_logLock) File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {safeLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _process?.Dispose();
    }
}
