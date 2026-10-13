[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$KeyId = 'pos-warehouses-dev-2026',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.2',
    [string]$OutputZip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot ("artifacts\modules\advanced-reports\$Version.zip")
}
$outputPath = [IO.Path]::GetFullPath($OutputZip)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El ZIP se debe guardar dentro de artifacts.'
}

$serverProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Reporting\Pos.Modules.Reporting.csproj'
$desktopProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Reporting.Desktop\Pos.Modules.Reporting.Desktop.csproj'
$publisherProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\Pos.Modules.Publisher.csproj'
dotnet build $serverProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del módulo de servidor de reportes.' }
dotnet build $desktopProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del módulo WPF de reportes.' }

$stage = Join-Path $repoRoot ("artifacts\module-staging\advanced-reports-$Version-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.Reporting\bin\Release\net10.0\Pos.Modules.Reporting.dll') $stage
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.Reporting.Desktop\bin\Release\net10.0-windows\Pos.Modules.Reporting.Desktop.dll') $stage
@'
{
  "files": [
    "Pos.Modules.Reporting.dll",
    "Pos.Modules.Reporting.Desktop.dll"
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $stage 'package-files.json') -Encoding utf8

$publisherOutput = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\bin\Release\net10.0\Pos.Modules.Publisher.dll'
dotnet build $publisherProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del publicador de módulos.' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
dotnet $publisherOutput build --manifest (Join-Path $repoRoot 'src\Modules\Pos.Modules.Reporting.Desktop\module.manifest.json') --stage $stage --files (Join-Path $stage 'package-files.json') --output $outputPath --key-id $KeyId --thumbprint $CertificateThumbprint --store CurrentUser
if ($LASTEXITCODE -ne 0) { throw 'No se pudo firmar el ZIP de Reportes avanzados.' }

$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'El certificado no tiene clave privada para firmar el módulo.' }
$publicKeyPath = Join-Path (Split-Path -Parent $outputPath) "$Version-publisher-public.pem"
dotnet $publisherOutput export-public-key --thumbprint $CertificateThumbprint --output $publicKeyPath --store CurrentUser
if ($LASTEXITCODE -ne 0) { throw 'No se pudo exportar la clave pública del publicador.' }

Write-Output "Paquete firmado: $outputPath"
Write-Output "Clave pública de verificación: $publicKeyPath"
Write-Output "Staging reproducible: $stage"
