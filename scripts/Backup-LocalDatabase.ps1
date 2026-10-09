[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PuntoDeVenta\Respaldos'),
    [ValidateRange(0, 3650)][int]$KeepLast = 0
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1')

$database = Get-PosDatabaseSettings
$bin = Get-PostgreSqlBinaryDirectory
$pgDump = Join-Path $bin 'pg_dump.exe'
$pgRestore = Join-Path $bin 'pg_restore.exe'
New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safeDatabaseName = $database.Database -replace '[^A-Za-z0-9_-]', '_'
$fileName = "pos-$safeDatabaseName-$stamp.backup"
$finalPath = Join-Path $DestinationDirectory $fileName
$partialPath = "$finalPath.partial"
$partialHashPath = "$finalPath.sha256.partial"

$env:PGPASSWORD = $database.Password
try {
    $dumpArgs = @('--no-password', '--format=custom', '--compress=6', '--host', $database.Host, '--port', [string]$database.Port, '--username', $database.Username, '--dbname', $database.Database, '--file', $partialPath)
    Invoke-PostgreSqlTool -Executable $pgDump -Arguments $dumpArgs -FailureMessage 'No se pudo crear el respaldo de PostgreSQL.'
    if (-not (Test-Path -LiteralPath $partialPath -PathType Leaf) -or (Get-Item -LiteralPath $partialPath).Length -eq 0) {
        throw 'PostgreSQL no generó un archivo de respaldo válido.'
    }

    $null = & $pgRestore --list $partialPath 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'El respaldo no pasó la comprobación de integridad de PostgreSQL.' }
    $hash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($partialHashPath, "$hash  $fileName`r`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $partialPath -Destination $finalPath
    Move-Item -LiteralPath $partialHashPath -Destination "$finalPath.sha256"
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $partialPath, $partialHashPath -Force -ErrorAction SilentlyContinue
    $database.Password = $null
}

if ($KeepLast -gt 0) {
    Get-ChildItem -LiteralPath $DestinationDirectory -Filter "pos-$safeDatabaseName-*.backup" -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $KeepLast |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            Remove-Item -LiteralPath "$($_.FullName).sha256" -Force -ErrorAction SilentlyContinue
        }
}

Write-Output "Respaldo creado y verificado: $finalPath"
