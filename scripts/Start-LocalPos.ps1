$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1')

$installRoot = Split-Path -Parent $PSScriptRoot
$server = Join-Path $installRoot 'server\Pos.Server.exe'
$desktop = Join-Path $installRoot 'desktop\Pos.Desktop.exe'
if (-not (Test-Path -LiteralPath $server -PathType Leaf) -or -not (Test-Path -LiteralPath $desktop -PathType Leaf)) {
    throw 'No se encontraron los componentes instalados del POS. Reinstala el paquete local.'
}
$database = Get-PosDatabaseSettings
$healthUri = 'http://127.0.0.1:5155/health/ready'
$startedHere = $false
$serverProcess = $null
$logDirectory = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Wait-ForPosServer {
    param([System.Diagnostics.Process]$Process)
    $deadline = (Get-Date).AddSeconds(60)
    do {
        if ($null -ne $Process -and $Process.HasExited) { throw "El servicio local se cerró durante el inicio. Revisa los registros en $logDirectory." }
        try { $response = Invoke-RestMethod -Uri $healthUri -TimeoutSec 2 } catch { $response = $null }
        if ($null -ne $response -and $response.status -eq 'ready') { return $response }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "El servicio local no respondió a tiempo. Revisa los registros en $logDirectory."
}

try {
    try { $live = Invoke-RestMethod -Uri 'http://127.0.0.1:5155/health/live' -TimeoutSec 2 } catch { $live = $null }
    if ($null -ne $live) {
        $null = Wait-ForPosServer -Process $null
    } else {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $stdout = Join-Path $logDirectory "servidor-$stamp.log"
        $stderr = Join-Path $logDirectory "servidor-$stamp-error.log"
        $oldEnvironment = @{
            ConnectionStrings__Pos = $env:ConnectionStrings__Pos
            ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
            ASPNETCORE_URLS = $env:ASPNETCORE_URLS
        }
        try {
            $env:ConnectionStrings__Pos = "Host=$($database.Host);Port=$($database.Port);Database=$($database.Database);Username=$($database.Username);Password=$($database.Password)"
            $env:ASPNETCORE_ENVIRONMENT = 'Production'
            $env:ASPNETCORE_URLS = 'http://127.0.0.1:5155'
            $serverProcess = Start-Process -FilePath $server -WorkingDirectory (Split-Path -Parent $server) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            $startedHere = $true
        } finally {
            foreach ($name in $oldEnvironment.Keys) {
                if ($null -eq $oldEnvironment[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
                else { Set-Item "Env:$name" $oldEnvironment[$name] }
            }
            $database.Password = $null
        }

        $null = Wait-ForPosServer -Process $serverProcess
    }

    Start-Process -FilePath $desktop -WorkingDirectory (Split-Path -Parent $desktop) | Out-Null
} catch {
    if ($startedHere -and $null -ne $serverProcess -and -not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id -Force }
    throw
} finally {
    $database.Password = $null
}
