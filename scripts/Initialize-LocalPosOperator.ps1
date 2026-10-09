$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1')

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Punto de Venta'
$server = Join-Path $installRoot 'server\Pos.Server.exe'
$configureOperator = Join-Path $PSScriptRoot 'Configure-InitialOperator.ps1'
if (-not (Test-Path -LiteralPath $server -PathType Leaf) -or -not (Test-Path -LiteralPath $configureOperator -PathType Leaf)) {
    throw 'No se encontró la instalación local del POS.'
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$database = Get-PosDatabaseSettings
$logDirectory = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdout = Join-Path $logDirectory "configuracion-inicial-$stamp.log"
$stderr = Join-Path $logDirectory "configuracion-inicial-$stamp-error.log"
$oldEnvironment = @{
    ConnectionStrings__Pos = $env:ConnectionStrings__Pos
    ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
    ASPNETCORE_URLS = $env:ASPNETCORE_URLS
}
$serverProcess = $null

try {
    $env:ConnectionStrings__Pos = "Host=$($database.Host);Port=$($database.Port);Database=$($database.Database);Username=$($database.Username);Password=$($database.Password)"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $baseUrl
    $serverProcess = Start-Process -FilePath $server -WorkingDirectory (Split-Path -Parent $server) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = (Get-Date).AddSeconds(60)
    do {
        if ($serverProcess.HasExited) { throw "El servicio se cerró durante la configuración. Revisa los registros en $logDirectory." }
        try { $ready = Invoke-RestMethod -Uri "$baseUrl/health/ready" -TimeoutSec 2 } catch { $ready = $null }
        if ($null -eq $ready -or $ready.status -ne 'ready') { Start-Sleep -Seconds 1 }
    } while (($null -eq $ready -or $ready.status -ne 'ready') -and (Get-Date) -lt $deadline)
    if ($null -eq $ready -or $ready.status -ne 'ready') { throw "El servicio local no respondió a tiempo. Revisa los registros en $logDirectory." }

    & $configureOperator -BaseUrl $baseUrl
} finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
        $serverProcess.WaitForExit()
    }
    foreach ($name in $oldEnvironment.Keys) {
        if ($null -eq $oldEnvironment[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $oldEnvironment[$name] }
    }
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    $database.Password = $null
}
