[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$KeyId = 'pos-warehouses-dev-2026',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$OutputZip
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot "artifacts\modules\thermal-receipts\$Version\thermal-receipts-$Version.zip"
}
$outputPath = [IO.Path]::GetFullPath($OutputZip)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El ZIP firmado debe guardarse dentro de artifacts.'
}
$serverProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.ThermalReceipts\Pos.Modules.ThermalReceipts.csproj'
$desktopProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.ThermalReceipts.Desktop\Pos.Modules.ThermalReceipts.Desktop.csproj'
$publisherProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\Pos.Modules.Publisher.csproj'
dotnet build $serverProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del módulo servidor de tickets.' }
dotnet build $desktopProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del módulo de escritorio de tickets.' }
dotnet build $publisherProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del publicador.' }
$stage = Join-Path $repoRoot ("artifacts\module-staging\thermal-receipts-$Version-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'migrations') | Out-Null
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.ThermalReceipts\bin\Release\net10.0\Pos.Modules.ThermalReceipts.dll') $stage
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.ThermalReceipts.Desktop\bin\Release\net10.0-windows\Pos.Modules.ThermalReceipts.Desktop.dll') $stage
Copy-Item (Join-Path $repoRoot 'src\Pos.Shared.Contracts\bin\Release\net10.0\Pos.Shared.Contracts.dll') $stage
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.ThermalReceipts\migrations\thermal-receipts-0001.json') (Join-Path $stage 'migrations')
@'
{
  "files": [
    "Pos.Modules.ThermalReceipts.dll",
    "Pos.Modules.ThermalReceipts.Desktop.dll",
    "Pos.Shared.Contracts.dll",
    "migrations/thermal-receipts-0001.json"
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $stage 'package-files.json') -Encoding utf8
$publisherOutput = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\bin\Release\net10.0\Pos.Modules.Publisher.dll'
$manifest = Join-Path $repoRoot 'src\Modules\Pos.Modules.ThermalReceipts\module.manifest.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
dotnet $publisherOutput build --manifest $manifest --stage $stage --files (Join-Path $stage 'package-files.json') --output $outputPath --key-id $KeyId --thumbprint $CertificateThumbprint --store CurrentUser
if ($LASTEXITCODE -ne 0) { throw 'No se pudo firmar el ZIP del módulo de tickets térmicos.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'El certificado indicado no tiene clave privada.' }
$publicKeyPath = Join-Path (Split-Path -Parent $outputPath) 'publisher-public.pem'
dotnet $publisherOutput export-public-key --thumbprint $CertificateThumbprint --output $publicKeyPath --store CurrentUser
if ($LASTEXITCODE -ne 0) { throw 'No se pudo exportar la clave pública del publicador.' }
& (Join-Path $PSScriptRoot 'Test-SignedModulePackage.ps1') -PackagePath $outputPath -PublicKeyPath $publicKeyPath
if ($LASTEXITCODE -ne 0) { throw 'La firma del ZIP no pasó la verificación.' }
Write-Output "Paquete firmado y verificado: $outputPath"
Write-Output "Clave pública: $publicKeyPath"
