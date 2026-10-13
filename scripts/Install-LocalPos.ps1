[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [switch]$ScheduleDailyBackup,
    [ValidatePattern('^(?:[01]\d|2[0-3]):[0-5]\d$')][string]$BackupTime = '20:00'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $scriptParent = Split-Path -Parent $PSScriptRoot
    if ((Test-Path (Join-Path $scriptParent 'server\Pos.Server.exe')) -and (Test-Path (Join-Path $scriptParent 'desktop\Pos.Desktop.exe'))) {
        $PackageDirectory = $scriptParent
    } else {
        $PackageDirectory = Join-Path $scriptParent 'artifacts\local-pos'
    }
}
$package = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$installCandidate = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Punto de Venta'
if ([IO.Path]::GetFullPath($package).TrimEnd('\') -ieq [IO.Path]::GetFullPath($installCandidate).TrimEnd('\')) {
    Add-Type -AssemblyName System.Windows.Forms
    $folderPicker = New-Object System.Windows.Forms.FolderBrowserDialog
    $folderPicker.Description = 'Selecciona la carpeta donde extrajiste el ZIP nuevo.'
    $folderPicker.ShowNewFolderButton = $false
    $folderPicker.SelectedPath = [Environment]::GetFolderPath('MyDocuments')
    try {
        if ($folderPicker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Output 'Actualización cancelada; no se modificaron los archivos instalados.'
            return
        }
        $selectedPackage = (Resolve-Path -LiteralPath $folderPicker.SelectedPath -ErrorAction Stop).Path
    } finally {
        $folderPicker.Dispose()
    }

    $selectedInstaller = Join-Path $selectedPackage 'scripts\Install-LocalPos.ps1'
    if (-not (Test-Path -LiteralPath $selectedInstaller -PathType Leaf)) {
        throw 'La carpeta seleccionada no contiene un paquete POS extraído. Extrae el ZIP completo y selecciona esa carpeta.'
    }
    if ([IO.Path]::GetFullPath($selectedPackage).TrimEnd('\') -ieq [IO.Path]::GetFullPath($installCandidate).TrimEnd('\')) {
        throw 'Selecciona la carpeta del paquete nuevo extraído, no la instalación permanente.'
    }

    & $selectedInstaller -PackageDirectory $selectedPackage -ScheduleDailyBackup:$ScheduleDailyBackup -BackupTime $BackupTime
    return
}
$packageScripts = Join-Path $package 'scripts'
$packageServer = Join-Path $package 'server\Pos.Server.exe'
$packageDesktop = Join-Path $package 'desktop\Pos.Desktop.exe'
if (-not (Test-Path $packageServer -PathType Leaf) -or -not (Test-Path $packageDesktop -PathType Leaf) -or
    -not (Test-Path (Join-Path $packageScripts 'Pos-DatabaseSupport.ps1') -PathType Leaf) -or
    -not (Test-Path (Join-Path $packageScripts 'Backup-LocalDatabase.ps1') -PathType Leaf)) {
    throw 'El paquete no está completo. Ejecuta primero Publish-LocalPos.ps1.'
}

function Get-LocalPosServerProcessId {
    param([int]$Port = 5155, [string]$HealthUrl = 'http://127.0.0.1:5155/health/live', [string]$CertificateSha256)
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($listeners.Count -eq 0) { return $null }
    if ($listeners.Count -ne 1) { throw 'Hay varios procesos escuchando en el puerto 5155. No se detuvo ninguno; revisa el servicio local.' }

    $ownerId = [int]$listeners[0]
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerId" -ErrorAction Stop
    if ($owner.Name -ne 'Pos.Server.exe') {
        throw "El puerto local 5155 pertenece a '$($owner.Name)', no al servidor POS. No se detuvo el proceso."
    }
    try {
        $health = Get-LocalPosServerHealth -HealthUrl $HealthUrl -CertificateSha256 $CertificateSha256
    } catch {
        throw 'Pos.Server.exe ocupa el puerto 5155 pero no responde como servidor POS. No se detuvo el proceso.'
    }
    if ($health.status -ne 'ok') { throw 'El proceso en el puerto 5155 no confirmó la identidad del servidor POS. No se detuvo.' }
    return $ownerId
}

function Get-LocalPosServerHealth {
    param([Parameter(Mandatory)][string]$HealthUrl, [string]$CertificateSha256)
    $uri = [uri]$HealthUrl
    if ($uri.Scheme -ne 'https') { return Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 4 }
    if ([string]$CertificateSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'El perfil del servidor no contiene una huella HTTPS válida para verificar el servicio antes de detenerlo.'
    }

    $expectedFingerprint = $CertificateSha256.ToUpperInvariant()
    $request = [System.Net.HttpWebRequest]::Create($uri)
    $request.Method = 'GET'
    $request.Timeout = 4000
    $request.ReadWriteTimeout = 4000
    $request.ServerCertificateValidationCallback = [PosPinnedCertificate]::CreateCallback($expectedFingerprint)
    $response = $request.GetResponse()
    try {
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
        finally { $reader.Dispose() }
    }
    finally { $response.Dispose() }
}

function Assert-LocalPosDesktopClosed {
    $desktopProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'Pos.Desktop.exe'" -ErrorAction SilentlyContinue)
    if ($desktopProcesses.Count -gt 0) {
        $details = ($desktopProcesses | ForEach-Object { "PID $($_.ProcessId): $($_.ExecutablePath)" }) -join '; '
        throw "Sigue activo Pos.Desktop.exe aunque no haya una ventana visible: $details. En Detener proceso por puerto.cmd elige la opción 2 para cerrarlo. El servidor no se ha detenido ni se inició el respaldo."
    }
}

function Invoke-PosDirectorySync {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$Mirror
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $copyArguments = @($Source, $Destination, '/COPY:DAT', '/DCOPY:DAT', '/R:2', '/W:1', '/XJ', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($Mirror) { $copyArguments += '/MIR' } else { $copyArguments += '/E' }
    & robocopy.exe @copyArguments | Out-Null
    $result = $LASTEXITCODE
    if ($result -ge 8) { throw "No se pudieron sincronizar archivos del POS (robocopy: $result): '$Source' -> '$Destination'." }
}

function Sync-PosInstallationFiles {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$BackupDirectory
    )

    $hasPreviousInstallation = Test-Path -LiteralPath $Destination -PathType Container
    if ($hasPreviousInstallation) {
        Invoke-PosDirectorySync -Source $Destination -Destination $BackupDirectory -Mirror
    }

    try {
        Invoke-PosDirectorySync -Source $Source -Destination $Destination -Mirror
    } catch {
        if ($hasPreviousInstallation -and (Test-Path -LiteralPath $BackupDirectory -PathType Container)) {
            try { Invoke-PosDirectorySync -Source $BackupDirectory -Destination $Destination -Mirror }
            catch { throw "Falló la actualización y tampoco se pudo restaurar la copia anterior. Conservé el respaldo en '$BackupDirectory'. Detalle de restauración: $($_.Exception.Message)" }
        } elseif (-not $hasPreviousInstallation -and (Test-Path -LiteralPath $Destination)) {
            Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Stop-LocalPosServer {
    param([int]$Port = 5155, [string]$HealthUrl = 'http://127.0.0.1:5155/health/live', [string]$CertificateSha256)
    $serverProcessId = Get-LocalPosServerProcessId -Port $Port -HealthUrl $HealthUrl -CertificateSha256 $CertificateSha256
    if ($null -eq $serverProcessId) { return }
    Stop-Process -Id $serverProcessId -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $remaining = Get-LocalPosServerProcessId -Port $Port -HealthUrl $HealthUrl -CertificateSha256 $CertificateSha256
        if ($null -eq $remaining) { return }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'El servidor POS no se detuvo en 15 segundos. No se instalaron archivos nuevos.'
}

. (Join-Path $packageScripts 'Pos-DatabaseSupport.ps1')
$profile = Get-PosInstallationProfile
$installationMode = if ($null -eq $profile) { 'Standalone' } else { [string]$profile.Mode }
if ($installationMode -notin @('Standalone', 'Server', 'ClientTerminal')) {
    throw "El modo de instalación '$installationMode' no es compatible con este actualizador."
}
$database = if ($installationMode -eq 'ClientTerminal') { $null } else { Get-PosDatabaseSettings }
if ($installationMode -ne 'ClientTerminal') {
    try { $env:POS_POSTGRES_BIN = Get-PostgreSqlBinaryDirectory }
    catch { throw "No se puede hacer una actualización segura sin pg_dump, pg_restore y psql. Instala PostgreSQL o corrige POS_POSTGRES_BIN. Detalle: $($_.Exception.Message)" }
}
$apiPort = 5155
$healthUrl = 'http://127.0.0.1:5155/health/live'
$certificateSha256 = $null
if ($installationMode -eq 'Server') {
    $serverUri = [uri]$profile.ApiBaseUrl
    $apiPort = $serverUri.Port
    $healthUrl = [uri]::new($serverUri, '/health/live').AbsoluteUri
    $certificateSha256 = [string]$profile.ServerCertificateSha256
    if ($serverUri.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace([string]$profile.ServerCertificatePath) -or
        [string]::IsNullOrWhiteSpace([string]$profile.ServerCertificatePassword)) {
        throw 'El perfil del servidor no tiene HTTPS y certificado configurados. No se detuvo el servidor.'
    }
}
Assert-LocalPosDesktopClosed

$installedServiceInfo = Get-CimInstance Win32_Service -Filter "Name='BalanzaProPosServer'" -ErrorAction SilentlyContinue
if ($null -ne $installedServiceInfo) {
    $registeredServerPath = [Environment]::ExpandEnvironmentVariables([string]$installedServiceInfo.PathName).Trim()
    $registeredServerPath = if ($registeredServerPath.StartsWith('"')) { $registeredServerPath.Split('"', [StringSplitOptions]::RemoveEmptyEntries)[0] } else { $registeredServerPath.Split(' ', 2, [StringSplitOptions]::RemoveEmptyEntries)[0] }
    $expectedServerPath = Join-Path $installCandidate 'server\Pos.Server.exe'
    if (-not [string]::Equals([IO.Path]::GetFullPath($registeredServerPath), [IO.Path]::GetFullPath($expectedServerPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw "El servicio POS está registrado desde '$registeredServerPath', pero la instalación permanente es '$expectedServerPath'. No se tocó el servicio ni la base. Instala/actualiza primero desde la carpeta permanente y vuelve a intentarlo."
    }
}

$target = $installCandidate
$targetParent = Split-Path -Parent $target
$stage = "$target.update-staging"
$previous = "$target.previous"
New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
foreach ($temporaryPath in @($stage, $previous)) {
    $fullTemporaryPath = [IO.Path]::GetFullPath($temporaryPath)
    if (-not $fullTemporaryPath.StartsWith([IO.Path]::GetFullPath($targetParent).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Se rechazó modificar una ruta de actualización fuera de la carpeta de instalación.'
    }
    if (Test-Path -LiteralPath $fullTemporaryPath) { Remove-Item -LiteralPath $fullTemporaryPath -Recurse -Force }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $stage -Recurse -Force
$stageScripts = Join-Path $stage 'scripts'

if ($installationMode -ne 'ClientTerminal') {
    # Check PostgreSQL authentication and create the backup while the local service remains available.
    # If pg_dump cannot connect, the POS is left running and the native PostgreSQL reason is reported.
    $backupScript = Join-Path $packageScripts 'Backup-LocalDatabase.ps1'
    & $backupScript -KeepLast 10
    $managedService = Get-Service -Name 'BalanzaProPosServer' -ErrorAction SilentlyContinue
    $serviceWasRunning = $null -ne $managedService -and $managedService.Status -eq 'Running'
    if ($serviceWasRunning) {
        try {
            Stop-Service -Name 'BalanzaProPosServer' -Force
            (Get-Service -Name 'BalanzaProPosServer').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            Write-Output 'Servicio BalanzaPro detenido después del respaldo; se actualizarán los archivos con la versión nueva.'
        } catch {
            try { Start-Service -Name 'BalanzaProPosServer'; (Get-Service -Name 'BalanzaProPosServer').WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
            catch { Write-Warning "No se pudo reactivar el servicio tras fallar su detención. Inícialo desde Administrar servicio POS.cmd. Detalle: $($_.Exception.Message)" }
            throw
        }
    }
    try {
        Stop-LocalPosServer -Port $apiPort -HealthUrl $healthUrl -CertificateSha256 $certificateSha256
    }
    catch {
        if ($serviceWasRunning) {
            try { Start-Service -Name 'BalanzaProPosServer'; (Get-Service -Name 'BalanzaProPosServer').WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
            catch { Write-Warning "No se pudo reactivar el servidor tras fallar la validación del puerto. Inícialo desde Administrar servicio POS.cmd. Detalle: $($_.Exception.Message)" }
        }
        throw
    }
}

if ($installationMode -ne 'ClientTerminal') {
    $oldConnection = $env:ConnectionStrings__Pos
    $oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $oldUrls = $env:ASPNETCORE_URLS
    $oldPairing = $env:PosSecurity__RequireTerminalPairing
    $oldCertificatePath = $env:ASPNETCORE_Kestrel__Certificates__Default__Path
    $oldCertificatePassword = $env:ASPNETCORE_Kestrel__Certificates__Default__Password
    try {
        $env:ConnectionStrings__Pos = "Host=$($database.Host);Port=$($database.Port);Database=$($database.Database);Username=$($database.Username);Password=$($database.Password)"
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        if ($installationMode -eq 'Server') {
            $env:ASPNETCORE_URLS = $profile.ApiBaseUrl.TrimEnd('/')
            $env:PosSecurity__RequireTerminalPairing = 'false'
            $env:ASPNETCORE_Kestrel__Certificates__Default__Path = [string]$profile.ServerCertificatePath
            $env:ASPNETCORE_Kestrel__Certificates__Default__Password = [string]$profile.ServerCertificatePassword
        } else {
            $env:ASPNETCORE_URLS = 'http://127.0.0.1:5155'
            $env:PosSecurity__RequireTerminalPairing = 'false'
        }
        & (Join-Path $stage 'server\Pos.Server.exe') --migrate
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo preparar el esquema de PostgreSQL. Los archivos instalados no se cambiaron y el respaldo preventivo quedó guardado; revisa la base antes de volver a abrir el POS.' }
    } catch {
        if ($serviceWasRunning) {
            try { Start-Service -Name 'BalanzaProPosServer'; (Get-Service -Name 'BalanzaProPosServer').WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
            catch { Write-Warning "No se pudo reactivar el servidor anterior tras fallar la migración. Inicia 'BalanzaProPosServer' desde Administrar servicio POS.cmd. Detalle: $($_.Exception.Message)" }
        }
        throw
    } finally {
        if ($null -eq $oldConnection) { Remove-Item Env:ConnectionStrings__Pos -ErrorAction SilentlyContinue } else { $env:ConnectionStrings__Pos = $oldConnection }
        if ($null -eq $oldEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment }
        if ($null -eq $oldUrls) { Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_URLS = $oldUrls }
        if ($null -eq $oldPairing) { Remove-Item Env:PosSecurity__RequireTerminalPairing -ErrorAction SilentlyContinue } else { $env:PosSecurity__RequireTerminalPairing = $oldPairing }
        if ($null -eq $oldCertificatePath) { Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Path -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $oldCertificatePath }
        if ($null -eq $oldCertificatePassword) { Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $oldCertificatePassword }
        if ($null -ne $database) { $database.Password = $null }
    }
}

try {
    Sync-PosInstallationFiles -Source $stage -Destination $target -BackupDirectory $previous
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    if ($serviceWasRunning) {
        try { Start-Service -Name 'BalanzaProPosServer'; (Get-Service -Name 'BalanzaProPosServer').WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
        catch { Write-Warning "No se pudo reactivar el servidor anterior después de fallar el reemplazo. Inícialo desde Administrar servicio POS.cmd. Detalle: $($_.Exception.Message)" }
    }
    throw
}
$targetScripts = Join-Path $target 'scripts'

$installedService = Get-Service -Name 'BalanzaProPosServer' -ErrorAction SilentlyContinue
if ($installationMode -ne 'ClientTerminal' -and $null -ne $installedService) {
    Start-Service -Name 'BalanzaProPosServer'
    (Get-Service -Name 'BalanzaProPosServer').WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    Write-Output 'El servicio POS está ejecutándose con los archivos actualizados.'
}

$shell = New-Object -ComObject WScript.Shell
$desktopExecutable = Join-Path $target 'desktop\Pos.Desktop.exe'
$desktopLauncher = Join-Path $target 'Iniciar POS.cmd'
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Punto de venta.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Punto de venta.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    $directory = Split-Path -Parent $shortcutPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $desktopLauncher
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = $desktopExecutable
    $shortcut.Description = 'Iniciar el punto de venta local'
    $shortcut.Save()
}

Write-Output "Punto de venta instalado para el usuario actual en '$target'. Usa el acceso directo 'Punto de venta' para abrirlo."
Write-Output 'Para crear el primer usuario, ejecuta Initialize-LocalPosOperator.ps1 desde la carpeta scripts de la instalación.'
Start-Process -FilePath $desktopLauncher -WorkingDirectory $target
Write-Output 'Actualización terminada. Se abrió la versión nueva; el servidor aplicará y verificará el esquema antes de mostrar el inicio de sesión.'
if ($ScheduleDailyBackup) {
    & (Join-Path $targetScripts 'Register-LocalBackupTask.ps1') -Time $BackupTime -KeepLast 14
}
