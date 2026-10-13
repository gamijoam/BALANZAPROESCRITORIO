[CmdletBinding()]
param(
    [string]$StageDirectory,
    [string]$OutputZip,
    [string]$KeyId = 'pos-warehouses-dev-2026',
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$PublisherPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publisherProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\Pos.Modules.Publisher.csproj'
$verifierScript = Join-Path $PSScriptRoot 'Test-SignedModulePackage.ps1'

function Select-ModuleStageDirectory {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = [Windows.Forms.FolderBrowserDialog]::new()
    $dialog.Description = 'Selecciona la carpeta de preparación del módulo. Debe contener module.manifest.json y package-files.json.'
    $dialog.ShowNewFolderButton = $false
    try {
        if ($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { return $null }
        return $dialog.SelectedPath
    } finally { $dialog.Dispose() }
}

if ([string]::IsNullOrWhiteSpace($StageDirectory)) {
    $StageDirectory = Select-ModuleStageDirectory
    if ([string]::IsNullOrWhiteSpace($StageDirectory)) { Write-Output 'Operación cancelada.'; exit 0 }
}
$stage = [IO.Path]::GetFullPath($StageDirectory)
$manifestPath = Join-Path $stage 'module.manifest.json'
$filesPath = Join-Path $stage 'package-files.json'
foreach ($requiredPath in @($manifestPath, $filesPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "La carpeta seleccionada no está lista para firmar. Falta: $requiredPath"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$manifest.id) -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
    throw 'El manifiesto debe declarar id y version antes de firmar.'
}
if ([string]::IsNullOrWhiteSpace($KeyId)) { throw 'Indica el ID de una clave pública que ya esté registrada en los servidores POS.' }

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $safeName = ([string]$manifest.id + '-' + [string]$manifest.version) -replace '[^A-Za-z0-9._-]', '-'
    $OutputZip = Join-Path (Split-Path -Parent $stage) "$safeName.zip"
}
$output = [IO.Path]::GetFullPath($OutputZip)
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if ($output.StartsWith($stage.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Guarda el ZIP fuera de la carpeta de preparación del módulo.'
}
$publicKeyPath = Join-Path $outputDirectory (([IO.Path]::GetFileNameWithoutExtension($output)) + '-publisher-public.pem')

$certificates = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } | Sort-Object Subject)
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $certificate = $certificates | Where-Object { $_.Thumbprint -eq $CertificateThumbprint } | Select-Object -First 1
} else {
    if ($certificates.Count -eq 0) { throw 'Esta cuenta de Windows no tiene certificados vigentes con clave privada para firmar.' }
    Write-Output 'Certificados disponibles para firmar (la clave privada permanece en Windows):'
    for ($index = 0; $index -lt $certificates.Count; $index++) {
        Write-Output ("[{0}] {1} | {2} | vence {3:yyyy-MM-dd}" -f $index, $certificates[$index].Subject, $certificates[$index].Thumbprint, $certificates[$index].NotAfter)
    }
    $selection = Read-Host 'Escribe el número del certificado'
    $selectedIndex = 0
    if (-not [int]::TryParse($selection, [ref]$selectedIndex) -or $selectedIndex -lt 0 -or $selectedIndex -ge $certificates.Count) {
        throw 'La selección del certificado no es válida.'
    }
    $certificate = $certificates[$selectedIndex]
}
if ($null -eq $certificate) { throw 'No se encontró el certificado solicitado o no tiene una clave privada disponible para esta cuenta de Windows.' }

if ([string]::IsNullOrWhiteSpace($PublisherPath)) {
    $portablePublisher = Join-Path $PSScriptRoot 'publisher\Pos.Modules.Publisher.exe'
    if (Test-Path -LiteralPath $portablePublisher -PathType Leaf) {
        $PublisherPath = $portablePublisher
    } else {
        if (-not (Test-Path -LiteralPath $publisherProject -PathType Leaf)) {
            throw 'No se encontró el publicador. Ejecuta este script desde el repositorio POS o indica -PublisherPath a Pos.Modules.Publisher.exe.'
        }
        & dotnet build $publisherProject --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la herramienta publicadora.' }
        $PublisherPath = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\bin\Release\net10.0\Pos.Modules.Publisher.dll'
    }
}
$PublisherPath = [IO.Path]::GetFullPath($PublisherPath)
if (-not (Test-Path -LiteralPath $PublisherPath -PathType Leaf)) { throw "No se encontró la herramienta publicadora: $PublisherPath" }

$publisherArguments = @(
    'build', '--manifest', $manifestPath, '--stage', $stage, '--files', $filesPath,
    '--output', $output, '--key-id', $KeyId, '--thumbprint', $certificate.Thumbprint, '--store', 'CurrentUser'
)
if ([IO.Path]::GetExtension($PublisherPath) -ieq '.dll') {
    & dotnet $PublisherPath @publisherArguments
} else {
    & $PublisherPath @publisherArguments
}
if ($LASTEXITCODE -ne 0) { throw 'El publicador no pudo crear el ZIP firmado.' }

$exportArguments = @('export-public-key', '--thumbprint', $certificate.Thumbprint, '--output', $publicKeyPath, '--store', 'CurrentUser')
if ([IO.Path]::GetExtension($PublisherPath) -ieq '.dll') {
    & dotnet $PublisherPath @exportArguments
} else {
    & $PublisherPath @exportArguments
}
if ($LASTEXITCODE -ne 0) { throw 'No se pudo exportar la clave pública de verificación.' }

if (-not (Test-Path -LiteralPath $verifierScript -PathType Leaf)) { throw "No se encontró el verificador de paquetes: $verifierScript" }
& $verifierScript -PackagePath $output -PublicKeyPath $publicKeyPath
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
Write-Output "Módulo firmado y verificado: $($manifest.id) $($manifest.version)"
Write-Output "Firmante: $KeyId"
Write-Output "ZIP: $output"
Write-Output "Clave pública (solo verificación): $publicKeyPath"
Write-Output "SHA-256: $hash"
