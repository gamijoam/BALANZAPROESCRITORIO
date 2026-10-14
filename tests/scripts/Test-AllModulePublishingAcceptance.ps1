[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$preparePath = Join-Path $repoRoot 'scripts\Prepare-ModulePackageForSigning.ps1'
$workflowPath = Join-Path $repoRoot '.github\workflows\publish-module.yml'
$catalogPath = Join-Path $repoRoot 'modulos\README.md'

foreach ($path in @($preparePath, $workflowPath, $catalogPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Falta el archivo requerido: $path" }
}

$prepare = Get-Content -LiteralPath $preparePath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$catalog = Get-Content -LiteralPath $catalogPath -Raw
$distributableIds = @('warehouses', 'thermal-receipts', 'advanced-reports', 'finance')

if ($prepare -notmatch "ValidateSet\([^\)]*'all'") {
    throw 'RED: el preparador todavía no admite publicar todos los módulos en una ejecución.'
}
if ($workflow -notmatch '(?m)^\s*-\s*all\s*$') {
    throw 'RED: GitHub Actions todavía no ofrece la selección «all».'
}
foreach ($moduleId in $distributableIds) {
    if ($catalog -notmatch [regex]::Escape($moduleId) -or $prepare -notmatch [regex]::Escape($moduleId)) {
        throw "El módulo distribuible '$moduleId' no está incluido en la publicación completa."
    }
}
$financeSettingsPackageEntry = "Source = 'src\Modules\Pos.Modules.Finance\migrations\finance-0002.json'; Package = 'migrations/finance-0002.json'"
if ($prepare -notmatch [regex]::Escape($financeSettingsPackageEntry)) {
    throw 'RED: el paquete de Finanzas no incluye la migración finance-0002 para los márgenes de precios.'
}
if ($workflow -notmatch 'PUBLISH_MODULE_INPUT' -or $workflow -notmatch 'expected_modules') {
    throw 'RED: el firmador no valida que recibió todos los paquetes solicitados.'
}

Write-Output 'OK: GitHub puede preparar y firmar todos los módulos distribuibles en una ejecución.'
