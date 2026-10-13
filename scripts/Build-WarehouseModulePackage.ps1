[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$KeyId = 'pos-warehouses-dev-2026',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.3.3',
    [string]$OutputZip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot ("artifacts\modules\warehouses\$Version.zip")
}
$outputPath = [IO.Path]::GetFullPath($OutputZip)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El ZIP se debe guardar dentro de artifacts.'
}

$serverProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Warehouses\Pos.Modules.Warehouses.csproj'
$desktopProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Warehouses.Desktop\Pos.Modules.Warehouses.Desktop.csproj'
$publisherProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\Pos.Modules.Publisher.csproj'
dotnet build $serverProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del módulo de servidor.' }
dotnet build $desktopProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del módulo WPF.' }

$stage = Join-Path $repoRoot ("artifacts\module-staging\warehouses-$Version-" + [Guid]::NewGuid().ToString('N'))
$migrationStage = Join-Path $stage 'migrations'
New-Item -ItemType Directory -Force -Path $migrationStage | Out-Null
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.Warehouses\bin\Release\net10.0\Pos.Modules.Warehouses.dll') $stage
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.Warehouses.Desktop\bin\Release\net10.0-windows\Pos.Modules.Warehouses.Desktop.dll') $stage
Copy-Item (Join-Path $repoRoot 'src\Modules\Pos.Modules.Warehouses\migrations\warehouses-0001.json') $migrationStage
@'
{
  "files": [
    "Pos.Modules.Warehouses.dll",
    "Pos.Modules.Warehouses.Desktop.dll",
    "migrations/warehouses-0001.json"
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $stage 'package-files.json') -Encoding utf8

$publisherOutput = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\bin\Release\net10.0\Pos.Modules.Publisher.dll'
dotnet build $publisherProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del publicador de módulos.' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
dotnet $publisherOutput build `
    --manifest (Join-Path $repoRoot 'src\Modules\Pos.Modules.Warehouses\module.manifest.json') `
    --stage $stage `
    --files (Join-Path $stage 'package-files.json') `
    --output $outputPath `
    --key-id $KeyId `
    --thumbprint $CertificateThumbprint `
    --store CurrentUser
if ($LASTEXITCODE -ne 0) { throw 'No se pudo firmar el ZIP de Almacenes.' }

$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'El certificado no tiene clave privada para firmar el módulo.' }
$publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
try {
    if ($null -eq $publicKey) { throw 'El certificado de publicación no contiene una clave RSA.' }
    $publicKeyPath = Join-Path (Split-Path -Parent $outputPath) "$Version-publisher-public.pem"
    [IO.File]::WriteAllText($publicKeyPath, $publicKey.ExportSubjectPublicKeyInfoPem(), [Text.Encoding]::ASCII)
} finally {
    if ($null -ne $publicKey) { $publicKey.Dispose() }
}

Write-Output "Paquete firmado: $outputPath"
Write-Output "Clave pública de verificación: $publicKeyPath"
Write-Output "Staging reproducible: $stage"
