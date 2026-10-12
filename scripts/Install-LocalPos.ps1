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
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'Instala .NET 10 antes de instalar el POS.' }
$installedRuntimes = & $dotnet --list-runtimes
foreach ($runtimeName in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App', 'Microsoft.WindowsDesktop.App')) {
    if (-not ($installedRuntimes | Where-Object { $_ -match "^$([regex]::Escape($runtimeName)) 10\." })) {
        throw "Falta el runtime .NET 10 '$runtimeName'. Instala el runtime correspondiente y vuelve a ejecutar." 
    }
}

$package = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$installCandidate = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Punto de Venta'
if ([IO.Path]::GetFullPath($package).TrimEnd('\') -ieq [IO.Path]::GetFullPath($installCandidate).TrimEnd('\')) {
    throw 'Ejecuta el instalador desde el paquete extraído, no desde la carpeta ya instalada.'
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
    $desktopProcesses = @(Get-Process -Name 'Pos.Desktop' -ErrorAction SilentlyContinue)
    if ($desktopProcesses.Count -gt 0) {
        throw 'Cierra todas las ventanas del Punto de venta antes de actualizar. Así se evita interrumpir una venta o una operación abierta.'
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

if ($installationMode -ne 'ClientTerminal') {
    # Check PostgreSQL authentication and create the backup while the local service remains available.
    # If pg_dump cannot connect, the POS is left running and the native PostgreSQL reason is reported.
    $backupScript = Join-Path $packageScripts 'Backup-LocalDatabase.ps1'
    & $backupScript -KeepLast 10
    Stop-LocalPosServer -Port $apiPort -HealthUrl $healthUrl -CertificateSha256 $certificateSha256
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

if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $previous }
try {
    Move-Item -LiteralPath $stage -Destination $target
} catch {
    if (-not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $target
    }
    throw
}
$targetScripts = Join-Path $target 'scripts'

$shell = New-Object -ComObject WScript.Shell
$desktopExecutable = Join-Path $target 'desktop\Pos.Desktop.exe'
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Punto de venta.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Punto de venta.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    $directory = Split-Path -Parent $shortcutPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $desktopExecutable
    $shortcut.WorkingDirectory = Split-Path -Parent $desktopExecutable
    $shortcut.IconLocation = $desktopExecutable
    $shortcut.Description = 'Iniciar el punto de venta local'
    $shortcut.Save()
}

Write-Output "Punto de venta instalado para el usuario actual en '$target'. Usa el acceso directo 'Punto de venta' para abrirlo."
Write-Output 'Para crear el primer usuario, ejecuta Initialize-LocalPosOperator.ps1 desde la carpeta scripts de la instalación.'
Start-Process -FilePath $desktopExecutable -WorkingDirectory (Split-Path -Parent $desktopExecutable)
Write-Output 'Actualización terminada. Se abrió la versión nueva; el servidor aplicará y verificará el esquema antes de mostrar el inicio de sesión.'
if ($ScheduleDailyBackup) {
    & (Join-Path $targetScripts 'Register-LocalBackupTask.ps1') -Time $BackupTime -KeepLast 14
}
