param([string]$ImporterScriptPath)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ImporterScriptPath)) {
    $ImporterScriptPath = Join-Path $PSScriptRoot '..\..\scripts\Import-LegacyReceivableOpeningBalances.ps1'
}
$ImporterScriptPath = (Resolve-Path -LiteralPath $ImporterScriptPath -ErrorAction SilentlyContinue).Path
if ([string]::IsNullOrWhiteSpace($ImporterScriptPath)) {
    throw 'RED: todavía no existe el importador de saldos iniciales.'
}

$scriptText = Get-Content -LiteralPath $ImporterScriptPath -Raw
$requiredText = @(
    'Pos-DatabaseSupport.ps1',
    'Pos-InvariantDecimalSupport.ps1',
    'Pos-JsonArraySupport.ps1',
    'ConvertFrom-PosJsonRows',
    'Convert-PosDecimalValue',
    'api/v1/customers/',
    'receivables/opening-balance',
    'idempotencyKey',
    'openingCount',
    'openingKey',
    'replayed',
    'sameNameCount',
    '$ambiguousNameOnly',
    'ServerCertificateSha256',
    '[switch]$Apply',
    '-AsSecureString',
    'CARGAR 81',
    '1260.0513',
    'novedades_stage'
)
foreach ($fragment in $requiredText) {
    if (-not $scriptText.Contains($fragment)) { throw "Falta el contrato requerido del importador: $fragment" }
}

$sqlMatch = [regex]::Match($scriptText, '(?s)\$sql\s*=\s*@''(.*?)''@')
if (-not $sqlMatch.Success) { throw 'El importador debe aislar la consulta de solo lectura.' }
if ($sqlMatch.Groups[1].Value -match '(?im)^\s*(INSERT|UPDATE|DELETE|TRUNCATE|ALTER|DROP)\b') {
    throw 'El importador no debe modificar PostgreSQL directamente.'
}
if ($scriptText -match '(?m)--command\s+\$' -or $scriptText -notmatch '(?m)--file\s+\$(queryFile|auditQueryFile)') {
    throw 'Las consultas PostgreSQL deben enviarse desde archivos UTF-8 para preservar las comillas de identificadores.'
}
if ($scriptText -match '\$json\s*\|\s*ConvertFrom-Json') {
    throw 'El importador debe separar las filas JSON explícitamente para Windows PowerShell 5.1.'
}
if ($scriptText -notmatch '\$ambiguousNameOnly\s*=\s*@\(' -or
    $scriptText -notmatch '\$_.sameNameCount\s*-ne\s*1') {
    throw 'Una deuda sin documento ni teléfono solo se puede asignar si el nombre identifica un único cliente activo.'
}
if ($sqlMatch.Groups[1].Value -notmatch 'p\."Id"' -or
    $scriptText -notmatch '\[IO\.File\]::WriteAllText\(\$queryFile,\s*\$sql,\s*\[Text\.UTF8Encoding\]::new\(\$false\)\)' -or
    $scriptText -notmatch '\[IO\.File\]::WriteAllText\(\$auditQueryFile,\s*\$auditSql,\s*\[Text\.UTF8Encoding\]::new\(\$false\)\)' -or
    $scriptText -notmatch "PGCLIENTENCODING = 'UTF8'" -or
    $scriptText -match '\[decimal\]::Parse\(\[string\]\$') {
    throw 'El importador debe mantener UTF-8, conservar identificadores y convertir importes sin depender de la cultura regional.'
}
Write-Output 'OK: el importador exige revision, confirmacion, TLS fijado, idempotencia y escritura por API.'
