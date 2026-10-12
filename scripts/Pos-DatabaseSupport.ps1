if (-not ('PosLocalDpapi' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class PosLocalDpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] input) { return Transform(input, true); }
    public static byte[] Unprotect(byte[] input) { return Transform(input, false); }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input == null) throw new ArgumentNullException("input");
        var pointer = Marshal.AllocHGlobal(input.Length);
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(input, 0, pointer, input.Length);
            var source = new DataBlob { Length = input.Length, Data = pointer };
            var ok = protect
                ? CryptProtectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr memory);
}
'@
}

if (-not ('PosPinnedCertificate' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class PosPinnedCertificate
{
    public static RemoteCertificateValidationCallback CreateCallback(string expectedSha256)
    {
        return delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null || String.IsNullOrWhiteSpace(expectedSha256)) return false;
            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(certificate.GetRawCertData());
                var actualSha256 = BitConverter.ToString(digest).Replace("-", String.Empty);
                return String.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        };
    }
}
'@
}

function Get-PosLocalDatabaseConfiguration {
    $configurationPath = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Configuracion\base-datos.dpapi'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) { return $null }
    try {
        $plaintext = [PosLocalDpapi]::Unprotect([IO.File]::ReadAllBytes($configurationPath))
    } catch {
        $windowsUser = if ([string]::IsNullOrWhiteSpace($env:USERDOMAIN)) { $env:USERNAME } else { "$env:USERDOMAIN\$env:USERNAME" }
        throw "No se pudo descifrar la conexión PostgreSQL protegida para '$windowsUser'. El POS y el actualizador deben ejecutarse con la misma cuenta de Windows; también puede estar dañada la configuración local. No se cambió la contraseña de PostgreSQL."
    }
    try {
        return [Text.Encoding]::UTF8.GetString($plaintext) | ConvertFrom-Json
    } finally {
        [Array]::Clear($plaintext, 0, $plaintext.Length)
    }
}

function Get-PosInstallationProfile {
    $profilePath = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Configuracion\deployment.dpapi'
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) { return $null }
    $plaintext = $null
    try {
        $plaintext = [PosLocalDpapi]::Unprotect([IO.File]::ReadAllBytes($profilePath))
        $profile = [Text.Encoding]::UTF8.GetString($plaintext) | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$profile.Mode) -or [string]::IsNullOrWhiteSpace([string]$profile.ApiBaseUrl)) {
            throw 'El perfil no contiene el modo y la dirección del servicio.'
        }
        return $profile
    } catch {
        throw 'No se pudo leer el perfil de instalación protegido. Ejecuta el actualizador con la misma cuenta de Windows que usa el POS.'
    } finally {
        if ($null -ne $plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    }
}

function Get-PosDatabaseSettings {
    $localConfiguration = Get-PosLocalDatabaseConfiguration
    if ($null -ne $localConfiguration) {
        foreach ($required in @('Host', 'Port', 'Database', 'Username', 'Password')) {
            if ([string]::IsNullOrWhiteSpace([string]$localConfiguration.$required)) {
                throw "La configuración protegida de PostgreSQL no tiene '$required'."
            }
        }
        return [pscustomobject]@{
            Host = [string]$localConfiguration.Host
            Port = [int]$localConfiguration.Port
            Database = [string]$localConfiguration.Database
            Username = [string]$localConfiguration.Username
            Password = [string]$localConfiguration.Password
        }
    }

    # Legacy fallback for development installs that have not yet migrated User Secrets to DPAPI.
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
    if (-not [string]::IsNullOrWhiteSpace($env:POS_POSTGRES_BIN)) {
        $candidate = $env:POS_POSTGRES_BIN
        if ((Test-Path -LiteralPath $candidate -PathType Container) -and
            (Test-Path (Join-Path $candidate 'pg_dump.exe')) -and
            (Test-Path (Join-Path $candidate 'pg_restore.exe')) -and
            (Test-Path (Join-Path $candidate 'psql.exe'))) { return $candidate }
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
    $output = @(& $Executable @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $detail = ($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' '
        if ([string]::IsNullOrWhiteSpace($detail)) { throw "$FailureMessage Código de salida: $exitCode." }
        throw "$FailureMessage Detalle de PostgreSQL: $detail"
    }
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

        $localConfiguration = Get-PosLocalDatabaseConfiguration
        if ($null -ne $localConfiguration) {
            $localConfiguration.Database = $DatabaseName
            $configurationPath = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Configuracion\base-datos.dpapi'
            $plaintext = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $localConfiguration -Compress))
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
        } else {
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
        }
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        $settings.Password = $null
    }
}
