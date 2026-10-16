[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Install', 'Configure', 'Reconfigure', 'Status', 'Start', 'Stop', 'Restart', 'Remove', 'EnsureRunning')]
    [string]$Action = 'Menu'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'BalanzaProPosServer'
$displayName = 'BalanzaPro POS Server'
$scriptRoot = $PSScriptRoot
$packageRoot = Split-Path -Parent $scriptRoot
$serverPath = Join-Path $packageRoot 'server\Pos.Server.exe'
$supportPath = Join-Path $scriptRoot 'Pos-DatabaseSupport.ps1'
$dataRoot = Join-Path $env:ProgramData 'BalanzaPro\PosServer'
$configPath = Join-Path $dataRoot 'service-config.dpapi'
$certificateDirectory = Join-Path $dataRoot 'Certificates'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = @(& sc.exe @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | ForEach-Object { $_.ToString().Trim() }) -join ' '
        throw "Windows no pudo ejecutar 'sc.exe $($Arguments -join ' ')'. $detail"
    }
    return $output
}

function Get-PosService {
    return Get-Service -Name $serviceName -ErrorAction SilentlyContinue
}

function Set-PosServiceRegistration {
    param([switch]$Create)

    $binaryPath = '"' + $serverPath + '" --windows-service'
    if ($Create) {
        $result = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
            Name = $serviceName
            DisplayName = $displayName
            PathName = $binaryPath
            ServiceType = [byte]16
            ErrorControl = [byte]1
            StartMode = 'Automatic'
            StartName = 'LocalSystem'
        }
    } else {
        $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
        $result = Invoke-CimMethod -InputObject $serviceInfo -MethodName Change -Arguments @{
            DisplayName = $displayName
            PathName = $binaryPath
            StartMode = 'Automatic'
            StartName = 'LocalSystem'
        }
    }

    if ($null -eq $result -or [int]$result.ReturnValue -ne 0) {
        $code = if ($null -eq $result) { 'sin respuesta' } else { [string]$result.ReturnValue }
        throw "Windows no pudo registrar la ruta del servidor POS mediante el administrador de servicios (código: $code)."
    }
}

function Get-PosServiceBinaryPath {
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if ($null -eq $serviceInfo -or [string]::IsNullOrWhiteSpace([string]$serviceInfo.PathName)) { return $null }
    $pathName = [Environment]::ExpandEnvironmentVariables([string]$serviceInfo.PathName).Trim()
    if ($pathName.StartsWith('"')) { return $pathName.Split('"', [StringSplitOptions]::RemoveEmptyEntries)[0] }
    return $pathName.Split(' ', 2, [StringSplitOptions]::RemoveEmptyEntries)[0]
}

function Get-PosServiceProcess {
    $native = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if ($null -eq $native -or $native.ProcessId -le 0) { return $null }
    return Get-CimInstance Win32_Process -Filter "ProcessId=$($native.ProcessId)" -ErrorAction SilentlyContinue
}

function Test-PosServiceBinaryStale {
    $process = Get-PosServiceProcess
    if ($null -eq $process -or [string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { return $false }
    $binaryPath = Get-PosServiceBinaryPath
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$process.ExecutablePath), [IO.Path]::GetFullPath($serverPath), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return ([datetime]$process.CreationDate) -lt (Get-Item -LiteralPath $binaryPath).LastWriteTime.AddSeconds(-2)
}

function Set-PosServiceConfiguration {
    if (-not (Test-Path -LiteralPath $supportPath -PathType Leaf)) { throw 'Faltan los scripts de soporte del POS junto a esta herramienta.' }
    if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) { throw "No se encontró el servidor del paquete: $serverPath" }
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    & icacls.exe $dataRoot /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo proteger la carpeta de configuración del servicio.' }
    . $supportPath
    $profile = Get-PosInstallationProfile
    if ($null -ne $profile -and [string]$profile.Mode -eq 'ClientTerminal') { throw 'Una terminal cliente no debe instalar un servidor POS local.' }
    $database = Get-PosDatabaseSettings
    $connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connectionBuilder['Host'] = [string]$database.Host
    $connectionBuilder['Port'] = [int]$database.Port
    $connectionBuilder['Database'] = [string]$database.Database
    $connectionBuilder['Username'] = [string]$database.Username
    $connectionBuilder['Password'] = [string]$database.Password
    $connectionBuilder['Timeout'] = 15
    $connectionBuilder['Command Timeout'] = 30

    $urls = 'http://127.0.0.1:5155'
    $licenseRenewalEndpoint = [Environment]::GetEnvironmentVariable('BALANZAPRO_LICENSE_RENEWAL_ENDPOINT', 'Machine')
    if (-not [string]::IsNullOrWhiteSpace($licenseRenewalEndpoint) -and $licenseRenewalEndpoint -notmatch '^https://[^/]+/api/v1/renewals$') {
        throw 'La URL de renovación debe usar HTTPS y terminar en /api/v1/renewals.'
    }
    $licenseEnforce = [Environment]::GetEnvironmentVariable('BALANZAPRO_LICENSE_ENFORCE', 'Machine')
    if ([string]::IsNullOrWhiteSpace($licenseEnforce)) { $licenseEnforce = 'false' }
    if ($licenseEnforce -notin @('true', 'false')) { throw 'BALANZAPRO_LICENSE_ENFORCE solo admite true o false.' }
    $certificatePath = $null
    $certificatePassword = $null
    if ($null -ne $profile -and [string]$profile.Mode -eq 'Server') {
        $urls = [string]$profile.ApiBaseUrl
        if ($urls -notmatch '^https://') { throw 'El perfil del servidor debe usar HTTPS.' }
        if (-not [string]::IsNullOrWhiteSpace([string]$profile.ServerCertificatePath)) {
            if (-not (Test-Path -LiteralPath ([string]$profile.ServerCertificatePath) -PathType Leaf)) { throw 'No se encontró el certificado HTTPS guardado en el perfil del servidor.' }
            New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null
            $certificatePath = Join-Path $certificateDirectory 'server.pfx'
            Copy-Item -LiteralPath ([string]$profile.ServerCertificatePath) -Destination $certificatePath -Force
            $certificatePassword = [string]$profile.ServerCertificatePassword
            & icacls.exe $certificatePath /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'No se pudieron limitar los permisos del certificado del servidor.' }
        }
    }

    $localPosRoot = Join-Path $env:LOCALAPPDATA 'POS'
    $moduleStoragePath = Join-Path $localPosRoot 'Modules'
    $moduleBackupPath = Join-Path $localPosRoot 'Backups'
    $logDirectory = Join-Path $dataRoot 'Logs'
    New-Item -ItemType Directory -Path $moduleStoragePath,$moduleBackupPath -Force | Out-Null
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    & icacls.exe $logDirectory /grant '*S-1-5-4:(OI)(CI)M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron configurar los permisos del registro de diagnósticos del servidor.' }
    & icacls.exe $localPosRoot /grant '*S-1-5-18:(OI)(CI)M' /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron conceder al servicio los permisos mínimos para los datos locales de módulos.' }
    $trustedKeys = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Security\TrustedModuleKeys.json'
    if (Test-Path -LiteralPath $trustedKeys -PathType Leaf) {
        & icacls.exe $trustedKeys /grant '*S-1-5-18:R' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo conceder acceso del servicio a las claves de confianza de módulos.' }
    }
    $postgresBin = Get-PostgreSqlBinaryDirectory
    $payload = [ordered]@{
        connectionString = [string]$connectionBuilder.ConnectionString
        urls = $urls
        certificatePath = $certificatePath
        certificatePassword = $certificatePassword
        moduleTrustKeyRingPath = $(if (Test-Path -LiteralPath $trustedKeys -PathType Leaf) { $trustedKeys } else { $null })
        licenseRenewalEndpoint = $licenseRenewalEndpoint
        licenseEnforce = $licenseEnforce
        moduleStoragePath = $moduleStoragePath
        moduleBackupPath = $moduleBackupPath
        logDirectory = $logDirectory
        postgresqlBinaryDirectory = $postgresBin
    }

    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    & icacls.exe $dataRoot /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo proteger la carpeta de configuración del servicio.' }
    $plain = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $payload -Compress))
    $protected = $null
    $temporaryPath = "$configPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $protected = [PosLocalDpapi]::ProtectMachine($plain)
        [IO.File]::WriteAllBytes($temporaryPath, $protected)
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $configPath, [NullString]::Value)
        } else {
            [IO.File]::Move($temporaryPath, $configPath)
        }
    } finally {
        [Array]::Clear($plain, 0, $plain.Length)
        if ($null -ne $protected) { [Array]::Clear($protected, 0, $protected.Length) }
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        $database.Password = $null
        $payload.connectionString = $null
        $connectionBuilder.Clear()
    }
}

function Install-PosService {
    param([switch]$DuringStartup)

    $permanentInstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Punto de Venta'
    if (-not [string]::Equals([IO.Path]::GetFullPath($packageRoot), [IO.Path]::GetFullPath($permanentInstallRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw "No instales el servicio desde el ZIP extraído. Primero ejecuta Actualizar POS.cmd y después abre Administrar servicio POS.cmd desde '$permanentInstallRoot'."
    }
    if (-not (Test-IsAdministrator)) { throw 'Instalar o servicio requiere permisos de administrador.' }
    if (-not $DuringStartup) {
        $desktopProcesses = @(Get-CimInstance Win32_Process -Filter "Name='Pos.Desktop.exe'" -ErrorAction SilentlyContinue)
        if ($desktopProcesses.Count -gt 0) { throw 'Cierra todas las ventanas del POS antes de instalar o reconfigurar el servicio. La base y las ventas no se modificaron.' }
    }
    $current = Get-PosService
    if ($null -ne $current -and $current.Status -eq 'Running') { Stop-Service -Name $serviceName -Force; $current.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    Set-PosServiceConfiguration
    if ($null -eq $current) {
        Set-PosServiceRegistration -Create
    } else {
        Set-PosServiceRegistration
    }
    $serviceRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    New-ItemProperty -LiteralPath $serviceRegistry -Name Environment -PropertyType MultiString -Value @("POS_SERVICE_CONFIG_PATH=$configPath") -Force | Out-Null
    New-ItemProperty -LiteralPath $serviceRegistry -Name DelayedAutoStart -PropertyType DWord -Value 1 -Force | Out-Null
    Invoke-ServiceControl -Arguments @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000') | Out-Null
    $sddl = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRRC;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)'
    Invoke-ServiceControl -Arguments @('sdset', $serviceName, $sddl) | Out-Null
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    Write-Output "Servicio '$displayName' instalado e iniciado. El inicio automático quedó configurado."
}

function Start-PosService {
    $service = Get-PosService
    if ($null -eq $service) { throw "El servicio $displayName no está instalado. Elige Instalar servicio primero." }
    $registeredPath = Get-PosServiceBinaryPath
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$registeredPath), [IO.Path]::GetFullPath($serverPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw "El servicio apunta a otra instalación ($registeredPath). No lo iniciaré desde este paquete para evitar levantar un servidor antiguo. Ejecuta Actualizar POS.cmd desde el paquete nuevo."
    }
    if ($service.Status -ne 'Running') {
        Start-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    } elseif (Test-PosServiceBinaryStale) {
        Restart-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
        Write-Output 'Se reinició el servicio porque su proceso estaba ejecutando una copia anterior del servidor.'
    }
    Write-Output "Servicio '$displayName': $((Get-Service -Name $serviceName).Status)."
}

function Stop-PosService {
    $service = Get-PosService
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Write-Output "Servicio '$displayName': detenido."
}

function Remove-PosService {
    if (-not (Test-IsAdministrator)) { throw 'Quitar el servicio requiere permisos de administrador.' }
    Stop-PosService
    if ($null -ne (Get-PosService)) { Invoke-ServiceControl -Arguments @('delete', $serviceName) | Out-Null }
    Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $certificateDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output 'Se quitó el registro del servicio y su configuración protegida. La base de datos y los datos de módulos se conservaron.'
}

function Show-PosServiceStatus {
    $service = Get-PosService
    if ($null -eq $service) { Write-Output 'El servicio BalanzaPro no está instalado.'; return }
    $native = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    Write-Output "Nombre: $displayName"
    Write-Output "Estado: $($service.Status)"
    Write-Output "Inicio: $($native.StartMode)"
    Write-Output "PID: $($native.ProcessId)"
    Write-Output "Ejecutable: $($native.PathName)"
    if ($native.ProcessId -gt 0) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($native.ProcessId)" -ErrorAction SilentlyContinue
        Write-Output "Ruta activa: $($process.ExecutablePath)"
        if (Test-PosServiceBinaryStale) { Write-Warning 'El proceso activo es anterior al ejecutable instalado. Reinicia el servicio antes de facturar.' }
    }
}

function Invoke-ElevatedAction {
    param([string]$RequestedAction = $Action)
    if ($RequestedAction -notin @('Install', 'Configure', 'Reconfigure', 'Remove', 'Stop', 'Restart') -or (Test-IsAdministrator)) { return $false }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Action', $RequestedAction)
    $arguments = @('-NoExit') + $arguments
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs
    return $true
}

if (Invoke-ElevatedAction) { exit 0 }
switch ($Action) {
    'Menu' {
        do {
            Clear-Host
            Write-Host 'ADMINISTRAR SERVICIO BALANZAPRO'
            Write-Host '1. Instalar / actualizar configuración y arrancar'
            Write-Host '2. Ver estado, PID y ejecutable activo'
            Write-Host '3. Reiniciar servicio'
            Write-Host '4. Detener servicio'
            Write-Host '5. Quitar servicio (conserva base y datos)'
            Write-Host '0. Salir'
            $choice = Read-Host 'Selecciona una opción'
            try {
                switch ($choice) {
                    '1' { if (Invoke-ElevatedAction -RequestedAction Install) { } else { Install-PosService } }
                    '2' { Show-PosServiceStatus }
                    '3' { if (Invoke-ElevatedAction -RequestedAction Restart) { } else { Stop-PosService; Start-PosService } }
                    '4' { if (Invoke-ElevatedAction -RequestedAction Stop) { } else { Stop-PosService } }
                    '5' { if (Invoke-ElevatedAction -RequestedAction Remove) { } else { Remove-PosService } }
                    '0' { return }
                    default { Write-Host 'Opción no válida.' }
                }
            } catch { Write-Host $_.Exception.Message -ForegroundColor Red }
            if ($choice -ne '0') { [void](Read-Host 'Presiona Enter para continuar') }
        } while ($true)
    }
    'Install' { Install-PosService }
    'Configure' { Set-PosServiceConfiguration }
    'Reconfigure' { Install-PosService -DuringStartup }
    'Status' { Show-PosServiceStatus }
    'Start' { Start-PosService }
    'EnsureRunning' { if ($null -ne (Get-PosService)) { Start-PosService } }
    'Stop' { Stop-PosService }
    'Restart' { Stop-PosService; Start-PosService }
    'Remove' { Remove-PosService }
}
