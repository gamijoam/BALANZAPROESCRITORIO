[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupFile,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9_]{0,62}$')][string]$TargetDatabaseName,
    [string]$AdminUsername = 'postgres',
    [switch]$Activate
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1')

$backup = (Resolve-Path -LiteralPath $BackupFile -ErrorAction Stop).Path
$checksumFile = "$backup.sha256"
if (-not (Test-Path -LiteralPath $checksumFile -PathType Leaf)) { throw 'El respaldo no tiene su archivo de verificación SHA-256.' }
$expectedHash = [regex]::Match((Get-Content -LiteralPath $checksumFile -Raw), '^[A-Fa-f0-9]{64}').Value.ToUpperInvariant()
if ([string]::IsNullOrWhiteSpace($expectedHash)) { throw 'El archivo de verificación SHA-256 no es válido.' }
$actualHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $expectedHash) { throw 'El respaldo está dañado o no coincide con su verificación SHA-256.' }

$database = Get-PosDatabaseSettings
$bin = Get-PostgreSqlBinaryDirectory
$pgRestore = Join-Path $bin 'pg_restore.exe'
$psql = Join-Path $bin 'psql.exe'
$createdb = Join-Path $bin 'createdb.exe'
$dropdb = Join-Path $bin 'dropdb.exe'
$secureAdminPassword = Read-Host "Contraseña del administrador PostgreSQL ($AdminUsername)" -AsSecureString
$adminPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureAdminPassword)
$createdTargetDatabase = $false

try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($adminPasswordPointer)
    $createArgs = @('--no-password', '--host', $database.Host, '--port', [string]$database.Port, '--username', $AdminUsername, '--maintenance-db', 'postgres', '--owner', $database.Username, $TargetDatabaseName)
    Invoke-PostgreSqlTool -Executable $createdb -Arguments $createArgs -FailureMessage "No se pudo crear '$TargetDatabaseName'. Si ya existe, no fue modificada."
    $createdTargetDatabase = $true

    $env:PGPASSWORD = $database.Password
    $restoreArgs = @('--no-password', '--exit-on-error', '--single-transaction', '--no-owner', '--no-privileges', '--host', $database.Host, '--port', [string]$database.Port, '--username', $database.Username, '--dbname', $TargetDatabaseName, $backup)
    Invoke-PostgreSqlTool -Executable $pgRestore -Arguments $restoreArgs -FailureMessage 'La restauración falló; se eliminará únicamente la base nueva de este intento.'

    $checkArgs = @('--no-psqlrc', '--no-password', '--tuples-only', '--no-align', '--host', $database.Host, '--port', [string]$database.Port, '--username', $database.Username, '--dbname', $TargetDatabaseName, '--command', 'SELECT count(*) FROM public."__EFMigrationsHistory";')
    $migrationCount = & $psql @checkArgs
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$migrationCount) -or [int]$migrationCount -lt 1) {
        throw 'La base restaurada no pudo verificar su historial de migraciones.'
    }

    $createdTargetDatabase = $false
    if ($Activate) {
        $confirmation = Read-Host "Para usar la base restaurada, escribe ACTIVAR $TargetDatabaseName"
        if ($confirmation -ceq "ACTIVAR $TargetDatabaseName") {
            Set-PosActiveDatabase -DatabaseName $TargetDatabaseName
            Write-Output "Base restaurada y activada: '$TargetDatabaseName'. La base original se conservó."
        } else {
            Write-Output "Restauración verificada en '$TargetDatabaseName'. La base activa no cambió."
        }
    } else {
        Write-Output "Restauración verificada en la base nueva '$TargetDatabaseName'. La base original no se modificó."
    }
} finally {
    if ($createdTargetDatabase) {
        $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($adminPasswordPointer)
        $cleanupArgs = @('--no-password', '--force', '--host', $database.Host, '--port', [string]$database.Port, '--username', $AdminUsername, $TargetDatabaseName)
        & $dropdb @cleanupArgs 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "No se pudo limpiar la base de prueba '$TargetDatabaseName'; elimínala manualmente después de revisar que sea la base nueva de este intento." }
    }
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($adminPasswordPointer)
    $secureAdminPassword.Dispose()
    $database.Password = $null
}
