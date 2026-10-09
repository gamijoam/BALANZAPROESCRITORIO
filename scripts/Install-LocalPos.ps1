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
if (-not (Test-Path $packageServer -PathType Leaf) -or -not (Test-Path $packageDesktop -PathType Leaf) -or -not (Test-Path (Join-Path $packageScripts 'Pos-DatabaseSupport.ps1') -PathType Leaf)) {
    throw 'El paquete no está completo. Ejecuta primero Publish-LocalPos.ps1.'
}

. (Join-Path $packageScripts 'Pos-DatabaseSupport.ps1')
$database = Get-PosDatabaseSettings
$existingServer = $null
try { $existingServer = Invoke-RestMethod -Uri 'http://127.0.0.1:5155/health/live' -TimeoutSec 2 } catch { }
if ($null -ne $existingServer) { throw 'Cierra el punto de venta y el servicio local antes de instalar o actualizar.' }
$target = $installCandidate
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $target -Recurse -Force
$targetScripts = Join-Path $target 'scripts'

$backupScript = Join-Path $targetScripts 'Backup-LocalDatabase.ps1'
& $backupScript -KeepLast 10

$oldConnection = $env:ConnectionStrings__Pos
$oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    $env:ConnectionStrings__Pos = "Host=$($database.Host);Port=$($database.Port);Database=$($database.Database);Username=$($database.Username);Password=$($database.Password)"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    & (Join-Path $target 'server\Pos.Server.exe') --migrate
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo preparar el esquema de PostgreSQL. El respaldo preventivo quedó guardado.' }
} finally {
    if ($null -eq $oldConnection) { Remove-Item Env:ConnectionStrings__Pos -ErrorAction SilentlyContinue } else { $env:ConnectionStrings__Pos = $oldConnection }
    if ($null -eq $oldEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment }
    $database.Password = $null
}

$shell = New-Object -ComObject WScript.Shell
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$launcher = Join-Path $targetScripts 'Start-LocalPos.ps1'
$arguments = "-NoProfile -WindowStyle Hidden -File `"$launcher`""
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Punto de venta.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Punto de venta.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    $directory = Split-Path -Parent $shortcutPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $powershell
    $shortcut.Arguments = $arguments
    $shortcut.WorkingDirectory = $targetScripts
    $shortcut.IconLocation = (Join-Path $target 'desktop\Pos.Desktop.exe')
    $shortcut.Description = 'Iniciar el punto de venta local'
    $shortcut.Save()
}

Write-Output "Punto de venta instalado para el usuario actual en '$target'. Usa el acceso directo 'Punto de venta' para abrirlo."
Write-Output 'Para crear el primer usuario, ejecuta Initialize-LocalPosOperator.ps1 desde la carpeta scripts de la instalación.'
if ($ScheduleDailyBackup) {
    & (Join-Path $targetScripts 'Register-LocalBackupTask.ps1') -Time $BackupTime -KeepLast 14
}
