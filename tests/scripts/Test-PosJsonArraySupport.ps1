param([string]$SupportScriptPath)

$ErrorActionPreference = 'Stop'
$supportPath = if ([string]::IsNullOrWhiteSpace($SupportScriptPath)) {
    Join-Path $PSScriptRoot '..\..\scripts\Pos-JsonArraySupport.ps1'
} else {
    $SupportScriptPath
}
if (-not (Test-Path -LiteralPath $supportPath -PathType Leaf)) {
    throw 'RED: todavía no existe el lector que separa las filas JSON en Windows PowerShell 5.1.'
}

. $supportPath
$rows = @(ConvertFrom-PosJsonRows -Json '[{"sourceId":"100","amount":"1.25"},{"sourceId":"101","amount":"2.50"}]')
if ($rows.Count -ne 2) { throw "Se esperaban dos filas individuales y se recibieron $($rows.Count)." }
if ($rows[0].GetType().FullName -eq 'System.Object[]' -or $rows[0].sourceId -ne '100' -or $rows[1].sourceId -ne '101') {
    throw 'Las filas o los identificadores quedaron agrupados dentro de un arreglo anidado.'
}

$single = @(ConvertFrom-PosJsonRows -Json '[{"sourceId":"105","amount":"0.0001"}]')
if ($single.Count -ne 1 -or $single[0].sourceId -ne '105') { throw 'No se conservó una respuesta de una sola fila.' }

$empty = @(ConvertFrom-PosJsonRows -Json '[]')
if ($empty.Count -ne 0) { throw 'Una lista JSON vacía debe producir cero filas.' }

$object = @(ConvertFrom-PosJsonRows -Json '{"sourceId":"106","amount":"3.75"}')
if ($object.Count -ne 1 -or $object[0].sourceId -ne '106') { throw 'No se conservó un objeto JSON raíz.' }

Write-Output 'OK: PowerShell 5.1 recibe cada cliente como una fila separada.'
