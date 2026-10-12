param([string]$SupportScriptPath)

$ErrorActionPreference = 'Stop'
$previousLocalAppData = $env:LOCALAPPDATA
$testLocalAppData = Join-Path $env:TEMP ('pos-dpapi-test-' + [guid]::NewGuid().ToString('N'))
$plainBytes = $null
$encryptedBytes = $null
$decryptedBytes = $null

try {
    $env:LOCALAPPDATA = $testLocalAppData
    if ([string]::IsNullOrWhiteSpace($SupportScriptPath)) {
        $SupportScriptPath = Join-Path $PSScriptRoot '..\..\scripts\Pos-DatabaseSupport.ps1'
    }
    . (Resolve-Path -LiteralPath $SupportScriptPath).Path

    $configuration = [pscustomobject]@{
        Host = '127.0.0.1'
        Port = 5432
        Database = 'pos_update_test'
        Username = 'pos_app_test'
        Password = 'test-only-value'
    }
    $plainBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $configuration -Compress))
    $encryptedBytes = [PosLocalDpapi]::Protect($plainBytes)
    $decryptedBytes = [PosLocalDpapi]::Unprotect($encryptedBytes)
    if ([Text.Encoding]::UTF8.GetString($decryptedBytes) -ne [Text.Encoding]::UTF8.GetString($plainBytes)) {
        throw 'La ida y vuelta DPAPI no conservó la configuración de prueba.'
    }

    $configurationPath = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Configuracion\base-datos.dpapi'
    New-Item -ItemType Directory -Path (Split-Path -Parent $configurationPath) -Force | Out-Null
    [IO.File]::WriteAllBytes($configurationPath, $encryptedBytes)
    $loaded = Get-PosDatabaseSettings
    if ($loaded.Database -ne $configuration.Database -or $loaded.Password -ne $configuration.Password) {
        throw 'La función Get-PosDatabaseSettings no leyó la configuración DPAPI de prueba.'
    }

    Write-Output 'OK: Windows PowerShell compiló DPAPI y leyó la configuración protegida.'
} finally {
    foreach ($buffer in @($plainBytes, $encryptedBytes, $decryptedBytes)) {
        if ($null -ne $buffer) { [Array]::Clear($buffer, 0, $buffer.Length) }
    }
    $env:LOCALAPPDATA = $previousLocalAppData
    Remove-Item -LiteralPath $testLocalAppData -Recurse -Force -ErrorAction SilentlyContinue
}
