param([string]$SupportScriptPath)

$ErrorActionPreference = 'Stop'
$supportPath = if ([string]::IsNullOrWhiteSpace($SupportScriptPath)) {
    Join-Path $PSScriptRoot '..\..\scripts\Pos-InvariantDecimalSupport.ps1'
} else {
    $SupportScriptPath
}
if (-not (Test-Path -LiteralPath $supportPath -PathType Leaf)) {
    throw 'RED: todavía no existe el conversor decimal independiente de la configuración regional.'
}

. $supportPath
$originalCulture = [Threading.Thread]::CurrentThread.CurrentCulture
try {
    [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('es-ES')
    $apiResponse = '{"amount":1260.0513,"smallAmount":0.0001}' | ConvertFrom-Json
    $localizedApiAmount = $apiResponse.amount.ToString('G', [Threading.Thread]::CurrentThread.CurrentCulture)
    $legacyConversionWasWrong = $false
    try {
        $legacyAmount = [decimal]::Parse($localizedApiAmount, [Globalization.CultureInfo]::InvariantCulture)
        $legacyConversionWasWrong = $legacyAmount -ne [decimal]::Parse('1260.0513', [Globalization.CultureInfo]::InvariantCulture)
    } catch { $legacyConversionWasWrong = $true }
    if (-not $legacyConversionWasWrong) { throw 'La prueba no reproduce el fallo de conversión regional anterior.' }

    $amount = Convert-PosDecimalValue -Value $apiResponse.amount -Context 'respuesta de cuentas por cobrar'
    $smallAmount = Convert-PosDecimalValue -Value $apiResponse.smallAmount -Context 'importe pequeño de prueba'
    if ($amount -ne [decimal]::Parse('1260.0513', [Globalization.CultureInfo]::InvariantCulture)) {
        throw 'El importe de la API cambió al convertirlo bajo configuración regional española.'
    }
    if ($smallAmount -ne [decimal]::Parse('0.0001', [Globalization.CultureInfo]::InvariantCulture)) {
        throw 'El importe pequeño de la API cambió al convertirlo bajo configuración regional española.'
    }

    $sourceAmount = Convert-PosDecimalValue -Value '1260.0513' -Context 'importe origen legado'
    if ($sourceAmount -ne $amount) { throw 'El importe textual del origen y el importe numérico de la API no coinciden.' }

    $failedAsExpected = $false
    try { $null = Convert-PosDecimalValue -Value 'importe-inválido' -Context 'origen de prueba' }
    catch { $failedAsExpected = $_.Exception.Message.Contains('origen de prueba') }
    if (-not $failedAsExpected) { throw 'El error de formato debe indicar qué campo del importe falló.' }
} finally {
    [Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
}

Write-Output 'OK: importes del servicio y del origen se convierten igual bajo cultura espanola.'
