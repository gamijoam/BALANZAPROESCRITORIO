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
$randomBytes = $null
$randomGenerator = $null
$appPassword = $null
$reuseApplicationPassword = $false

try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)

    & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U postgres -d postgres -c 'SELECT 1;' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Falló la autenticación de PostgreSQL; no se modificó ninguna base.' }

    $databaseExists = & $psql -X -q -A -t -h $HostName -p $Port -U postgres -d postgres -c "SELECT 1 FROM pg_catalog.pg_database WHERE datname = '$DatabaseName';"
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo consultar la lista de bases de PostgreSQL.' }
    $databaseAlreadyExists = [string]::Join('', @($databaseExists)).Trim() -eq '1'
    if ($databaseAlreadyExists) {
        $owner = & $psql -X -q -A -t -h $HostName -p $Port -U postgres -d postgres -c "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_catalog.pg_database WHERE datname = '$DatabaseName';"
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo comprobar el propietario de la base.' }
        if ([string]::Join('', @($owner)).Trim() -ne $DatabaseRole) {
            throw 'La base indicada no pertenece al rol de aplicación del POS; se dejó sin cambios.'
        }

        $existingConfiguration = Get-PosLocalDatabaseConfiguration
        if ($null -eq $existingConfiguration) {
            $secretId = 'f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5'
            $legacySecretPath = Join-Path $env:APPDATA "Microsoft\UserSecrets\$secretId\secrets.json"
            if (Test-Path -LiteralPath $legacySecretPath -PathType Leaf) {
                $existingConfiguration = Get-PosDatabaseSettings
            }
        }
        if ($null -ne $existingConfiguration -and
            [string]$existingConfiguration.Host -eq $HostName -and
            [int]$existingConfiguration.Port -eq $Port -and
            [string]$existingConfiguration.Database -eq $DatabaseName -and
            [string]$existingConfiguration.Username -eq $DatabaseRole -and
            -not [string]::IsNullOrWhiteSpace([string]$existingConfiguration.Password)) {
            $env:PGPASSWORD = [string]$existingConfiguration.Password
            & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U $DatabaseRole -d $DatabaseName -c 'SELECT 1;' | Out-Null
            if ($LASTEXITCODE -eq 0) {
                $appPassword = [string]$existingConfiguration.Password
                $reuseApplicationPassword = $true
            }
            $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        }
    }

    if (-not $reuseApplicationPassword) {
        $randomBytes = New-Object byte[] 32
        $randomGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
        $randomGenerator.GetBytes($randomBytes)
        $appPassword = [BitConverter]::ToString($randomBytes).Replace('-', '').ToLowerInvariant()

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

        if (-not $databaseAlreadyExists) {
            & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U postgres -d postgres -c "CREATE DATABASE `"$DatabaseName`" OWNER `"$DatabaseRole`";" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la base local del POS.' }
        }
    }

    # New tables created by EF migrations run under the application role and are
    # therefore owned by it. Repair the known terminal metadata tables that may
    # have been created earlier by postgres so the module planner can read them.
    # This is deliberately read-only access and never changes the app role password.
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    foreach ($tableName in @('paired_terminals', 'terminal_client_versions')) {
        $tableExists = & $psql -X -q -A -t -h $HostName -p $Port -U postgres -d $DatabaseName -c "SELECT to_regclass('public.$tableName') IS NOT NULL;"
        if ($LASTEXITCODE -ne 0) { throw "No se pudo comprobar la tabla '$tableName' con la cuenta administrativa." }
        if ([string]::Join('', @($tableExists)).Trim() -ne 't') { continue }

        $hasSelect = & $psql -X -q -A -t -h $HostName -p $Port -U postgres -d $DatabaseName -c "SELECT has_table_privilege('$DatabaseRole', 'public.$tableName', 'SELECT');"
        if ($LASTEXITCODE -ne 0) { throw "No se pudo comprobar el permiso de lectura de '$tableName'." }
        if ([string]::Join('', @($hasSelect)).Trim() -eq 't') { continue }

        & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U postgres -d $DatabaseName -c "GRANT SELECT ON TABLE public.$tableName TO `"$DatabaseRole`";" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "No se pudo conceder lectura de '$tableName' al usuario de aplicación '$DatabaseRole'." }
    }

    $env:PGPASSWORD = $appPassword
    & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U $DatabaseRole -d $DatabaseName -c 'SELECT 1;' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'El rol de aplicación no pudo conectarse a la base del POS.' }
    foreach ($tableName in @('paired_terminals', 'terminal_client_versions')) {
        $tableExists = & $psql -X -q -A -t -h $HostName -p $Port -U $DatabaseRole -d $DatabaseName -c "SELECT to_regclass('public.$tableName') IS NOT NULL;"
        if ($LASTEXITCODE -ne 0) { throw "El usuario de aplicación no pudo comprobar la tabla '$tableName'." }
        if ([string]::Join('', @($tableExists)).Trim() -ne 't') { continue }
        & $psql -X -q -v ON_ERROR_STOP=1 -h $HostName -p $Port -U $DatabaseRole -d $DatabaseName -c "SELECT 1 FROM public.$tableName LIMIT 0;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "El usuario de aplicación todavía no puede leer '$tableName' después de reparar permisos." }
    }

    $configurationDirectory = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Configuracion'
    $configurationPath = Join-Path $configurationDirectory 'base-datos.dpapi'
    New-Item -ItemType Directory -Path $configurationDirectory -Force | Out-Null
    $configuration = [pscustomobject]@{
        Host = $HostName
        Port = $Port
        Database = $DatabaseName
        Username = $DatabaseRole
        Password = $appPassword
    }
    $plaintext = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $configuration -Compress))
    $protected = [PosLocalDpapi]::Protect($plaintext)
    $temporaryPath = "$configurationPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporaryPath, $protected)
        Move-Item -LiteralPath $temporaryPath -Destination $configurationPath -Force
    } finally {
        [Array]::Clear($plaintext, 0, $plaintext.Length)
        [Array]::Clear($protected, 0, $protected.Length)
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }

    $credentialAction = if ($reuseApplicationPassword) { 'se conservó la contraseña de aplicación existente' } else { 'se configuró una contraseña de aplicación nueva' }
    Write-Output "PostgreSQL listo: base '$DatabaseName', rol '$DatabaseRole'; $credentialAction y la conexión se guardó cifrada para el usuario actual de Windows."
} catch {
    Write-Error ("La configuración falló cerca de la línea {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
    throw
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    if ($null -ne $plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    if ($null -ne $protected) { [Array]::Clear($protected, 0, $protected.Length) }
    $appPassword = $null
    if ($null -ne $randomBytes) { [Array]::Clear($randomBytes, 0, $randomBytes.Length) }
    if ($null -ne $randomGenerator) { $randomGenerator.Dispose() }
    $securePassword.Dispose()
}
