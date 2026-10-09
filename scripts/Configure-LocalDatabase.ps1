[CmdletBinding()]
param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 5432,
    [string]$DatabaseName = 'pos_dev',
    [string]$DatabaseRole = 'pos_app'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1')
$pgBin = Get-PostgreSqlBinaryDirectory
$psql = Join-Path $pgBin 'psql.exe'

if ($DatabaseName -notmatch '^[a-z][a-z0-9_]{0,62}$' -or $DatabaseRole -notmatch '^[a-z][a-z0-9_]{0,62}$') {
    throw 'La base y el rol deben usar minúsculas, números y guiones bajos, con formato válido de PostgreSQL.'
}

$securePassword = Read-Host 'Introduce la contraseña del usuario postgres' -AsSecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
$randomBytes = New-Object byte[] 32
$randomGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
$randomGenerator.GetBytes($randomBytes)
$appPassword = [BitConverter]::ToString($randomBytes).Replace('-', '').ToLowerInvariant()

try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)

    & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U postgres -d postgres -c 'SELECT 1;' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Falló la autenticación de PostgreSQL; no se modificó ninguna base.' }

    $roleSql = @'
DO $role$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '__ROLE__') THEN
        EXECUTE format('ALTER ROLE __ROLE__ WITH LOGIN PASSWORD %L', '__PASSWORD__');
    ELSE
        EXECUTE format('CREATE ROLE __ROLE__ WITH LOGIN PASSWORD %L', '__PASSWORD__');
    END IF;
END
$role$;
'@
    $roleSql = $roleSql.Replace('__ROLE__', $DatabaseRole).Replace('__PASSWORD__', $appPassword)
    $roleSql | & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U postgres -d postgres
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear o actualizar el rol de aplicación.' }

    $databaseExists = & $psql -X -q -A -t -h $HostName -p $Port -U postgres -d postgres -c "SELECT 1 FROM pg_catalog.pg_database WHERE datname = '$DatabaseName';"
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo consultar la lista de bases de PostgreSQL.' }
    if ([string]::Join('', @($databaseExists)).Trim() -ne '1') {
        & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U postgres -d postgres -c "CREATE DATABASE `"$DatabaseName`" OWNER `"$DatabaseRole`";" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la base local del POS.' }
    }

    $owner = & $psql -X -q -A -t -h $HostName -p $Port -U postgres -d postgres -c "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_catalog.pg_database WHERE datname = '$DatabaseName';"
    if ($LASTEXITCODE -ne 0 -or [string]::Join('', @($owner)).Trim() -ne $DatabaseRole) {
        throw 'La base indicada no pertenece al rol de aplicación del POS; se dejó sin cambios.'
    }

    $env:PGPASSWORD = $appPassword
    & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U $DatabaseRole -d $DatabaseName -c 'SELECT 1;' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'El rol de aplicación no pudo conectarse a la base del POS.' }

    $userSecretsId = 'f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5'
    $secretsDirectory = Join-Path $env:APPDATA "Microsoft\UserSecrets\$userSecretsId"
    $secretsPath = Join-Path $secretsDirectory 'secrets.json'
    New-Item -ItemType Directory -Path $secretsDirectory -Force | Out-Null
    $secrets = if (Test-Path -LiteralPath $secretsPath) {
        Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    if (-not $secrets) { $secrets = [pscustomobject]@{} }
    $connectionStrings = $secrets.ConnectionStrings
    if (-not $connectionStrings) {
        $connectionStrings = [pscustomobject]@{}
        $secrets | Add-Member -MemberType NoteProperty -Name ConnectionStrings -Value $connectionStrings
    }
    $connectionStrings | Add-Member -MemberType NoteProperty -Name Pos -Value "Host=$HostName;Port=$Port;Database=$DatabaseName;Username=$DatabaseRole;Password=$appPassword;Timeout=15;Command Timeout=30" -Force
    $json = ConvertTo-Json -InputObject $secrets -Depth 20
    [IO.File]::WriteAllText($secretsPath, $json, [Text.UTF8Encoding]::new($false))

    Write-Output "PostgreSQL listo: base '$DatabaseName', rol '$DatabaseRole'; la conexión se guardó en la configuración segura del usuario de Windows."
} catch {
    Write-Error ("La configuración falló cerca de la línea {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
    throw
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    $appPassword = $null
    [Array]::Clear($randomBytes, 0, $randomBytes.Length)
    $randomGenerator.Dispose()
    $securePassword.Dispose()
}
