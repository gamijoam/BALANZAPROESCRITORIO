function Get-PosDatabaseSettings {
    $secretId = 'f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5'
    $secretPath = Join-Path $env:APPDATA "Microsoft\UserSecrets\$secretId\secrets.json"
    if (-not (Test-Path -LiteralPath $secretPath -PathType Leaf)) {
        throw 'No se encontró la configuración segura de PostgreSQL. Ejecuta primero Configure-LocalDatabase.ps1 con el usuario de Windows que usará el POS.'
    }

    $document = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
    $connectionString = $document.ConnectionStrings.Pos
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw 'La configuración segura no contiene la conexión de PostgreSQL del POS.'
    }
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connectionStringProperty = $builder.GetType().GetProperty('ConnectionString')
    $connectionStringProperty.SetValue($builder, $connectionString)
    foreach ($required in @('Host', 'Port', 'Database', 'Username', 'Password')) {
        if (-not $builder.ContainsKey($required) -or [string]::IsNullOrWhiteSpace([string]$builder[$required])) {
        throw "La conexión de PostgreSQL no tiene configurado '$required'."
        }
    }

    [pscustomobject]@{
        Host = [string]$builder['Host']
        Port = [int]$builder['Port']
        Database = [string]$builder['Database']
        Username = [string]$builder['Username']
        Password = [string]$builder['Password']
    }
}

function Get-PostgreSqlBinaryDirectory {
    if (-not [string]::IsNullOrWhiteSpace($env:POS_POSTGRES_BIN) -and (Test-Path -LiteralPath $env:POS_POSTGRES_BIN -PathType Container)) {
        $candidate = $env:POS_POSTGRES_BIN
        if ((Test-Path (Join-Path $candidate 'pg_dump.exe')) -and (Test-Path (Join-Path $candidate 'pg_restore.exe'))) { return $candidate }
        throw 'La carpeta POS_POSTGRES_BIN no contiene pg_dump.exe y pg_restore.exe.'
    }

    $root = Join-Path $env:ProgramFiles 'PostgreSQL'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'No se encontró PostgreSQL en Archivos de programa.' }
    $directories = Get-ChildItem -LiteralPath $root -Directory | Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending
    foreach ($directory in $directories) {
        $bin = Join-Path $directory.FullName 'bin'
        if ((Test-Path (Join-Path $bin 'pg_dump.exe')) -and (Test-Path (Join-Path $bin 'pg_restore.exe')) -and (Test-Path (Join-Path $bin 'psql.exe'))) {
            return $bin
        }
    }
    throw 'No se encontraron pg_dump.exe, pg_restore.exe y psql.exe en la instalación de PostgreSQL.'
}

function Invoke-PostgreSqlTool {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$FailureMessage)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
}

function Set-PosActiveDatabase {
    param([Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9_]{0,62}$')][string]$DatabaseName)
    $serverIsRunning = $false
    try { $null = Invoke-RestMethod -Uri 'http://127.0.0.1:5155/health/live' -TimeoutSec 2; $serverIsRunning = $true } catch { }
    if ($serverIsRunning) { throw 'Cierra el punto de venta y el servicio local antes de cambiar la base activa.' }

    $settings = Get-PosDatabaseSettings
    $bin = Get-PostgreSqlBinaryDirectory
    $psql = Join-Path $bin 'psql.exe'
    $env:PGPASSWORD = $settings.Password
    try {
        $arguments = @('--no-psqlrc', '--no-password', '--tuples-only', '--no-align', '--host', $settings.Host, '--port', [string]$settings.Port, '--username', $settings.Username, '--dbname', $DatabaseName, '--command', 'SELECT count(*) FROM public."__EFMigrationsHistory";')
        $migrationCount = & $psql @arguments
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$migrationCount) -or [int]$migrationCount -lt 1) {
            throw 'La base restaurada no está disponible o no tiene migraciones aplicadas.'
        }

        $secretId = 'f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5'
        $secretPath = Join-Path $env:APPDATA "Microsoft\UserSecrets\$secretId\secrets.json"
        $secrets = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connectionStringProperty = $builder.GetType().GetProperty('ConnectionString')
        $connectionStringProperty.SetValue($builder, [string]$secrets.ConnectionStrings.Pos)
        $builder['Database'] = $DatabaseName
        $secrets.ConnectionStrings.Pos = [string]$connectionStringProperty.GetValue($builder)
        $temporaryPath = "$secretPath.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            [IO.File]::WriteAllText($temporaryPath, (ConvertTo-Json -InputObject $secrets -Depth 20), [Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporaryPath -Destination $secretPath -Force
        } finally { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        $settings.Password = $null
    }
}
