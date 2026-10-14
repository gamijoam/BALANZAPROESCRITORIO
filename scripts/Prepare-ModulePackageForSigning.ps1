[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('warehouses', 'thermal-receipts', 'advanced-reports', 'finance', 'all')]
    [string]$Module,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\module-release-input'),
    [string]$KeyId = 'pos-publisher-vps-2026'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Los archivos temporales de publicación deben generarse dentro de artifacts.'
}
if ($KeyId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$') { throw 'El identificador de publicador no es válido.' }

$moduleIds = if ($Module -eq 'all') { @('warehouses', 'thermal-receipts', 'advanced-reports', 'finance') } else { @($Module) }
$specs = foreach ($moduleId in $moduleIds) {
    switch ($moduleId) {
    'warehouses' {
        [pscustomobject]@{
            Id = 'warehouses'
            Manifest = 'src\Modules\Pos.Modules.Warehouses\module.manifest.json'
            BuildProjects = @(
                'src\Modules\Pos.Modules.Warehouses\Pos.Modules.Warehouses.csproj',
                'src\Modules\Pos.Modules.Warehouses.Desktop\Pos.Modules.Warehouses.Desktop.csproj'
            )
            Files = @(
                @{ Source = 'src\Modules\Pos.Modules.Warehouses\bin\Release\net10.0\Pos.Modules.Warehouses.dll'; Package = 'Pos.Modules.Warehouses.dll' },
                @{ Source = 'src\Modules\Pos.Modules.Warehouses.Desktop\bin\Release\net10.0-windows\Pos.Modules.Warehouses.Desktop.dll'; Package = 'Pos.Modules.Warehouses.Desktop.dll' },
                @{ Source = 'src\Modules\Pos.Modules.Warehouses\migrations\warehouses-0001.json'; Package = 'migrations/warehouses-0001.json' }
            )
        }
    }
    'thermal-receipts' {
        [pscustomobject]@{
            Id = 'thermal-receipts'
            Manifest = 'src\Modules\Pos.Modules.ThermalReceipts\module.manifest.json'
            BuildProjects = @(
                'src\Modules\Pos.Modules.ThermalReceipts\Pos.Modules.ThermalReceipts.csproj',
                'src\Modules\Pos.Modules.ThermalReceipts.Desktop\Pos.Modules.ThermalReceipts.Desktop.csproj'
            )
            Files = @(
                @{ Source = 'src\Modules\Pos.Modules.ThermalReceipts\bin\Release\net10.0\Pos.Modules.ThermalReceipts.dll'; Package = 'Pos.Modules.ThermalReceipts.dll' },
                @{ Source = 'src\Modules\Pos.Modules.ThermalReceipts.Desktop\bin\Release\net10.0-windows\Pos.Modules.ThermalReceipts.Desktop.dll'; Package = 'Pos.Modules.ThermalReceipts.Desktop.dll' },
                @{ Source = 'src\Pos.Shared.Contracts\bin\Release\net10.0\Pos.Shared.Contracts.dll'; Package = 'Pos.Shared.Contracts.dll' },
                @{ Source = 'src\Modules\Pos.Modules.ThermalReceipts\migrations\thermal-receipts-0001.json'; Package = 'migrations/thermal-receipts-0001.json' }
            )
        }
    }
    'advanced-reports' {
        [pscustomobject]@{
            Id = 'advanced-reports'
            Manifest = 'src\Modules\Pos.Modules.Reporting.Desktop\module.manifest.json'
            BuildProjects = @(
                'src\Modules\Pos.Modules.Reporting\Pos.Modules.Reporting.csproj',
                'src\Modules\Pos.Modules.Reporting.Desktop\Pos.Modules.Reporting.Desktop.csproj'
            )
            Files = @(
                @{ Source = 'src\Modules\Pos.Modules.Reporting\bin\Release\net10.0\Pos.Modules.Reporting.dll'; Package = 'Pos.Modules.Reporting.dll' },
                @{ Source = 'src\Modules\Pos.Modules.Reporting.Desktop\bin\Release\net10.0-windows\Pos.Modules.Reporting.Desktop.dll'; Package = 'Pos.Modules.Reporting.Desktop.dll' }
            )
        }
    }
    'finance' {
        [pscustomobject]@{
            Id = 'finance'
            Manifest = 'src\Modules\Pos.Modules.Finance\module.manifest.json'
            BuildProjects = @(
                'src\Modules\Pos.Modules.Finance\Pos.Modules.Finance.csproj',
                'src\Modules\Pos.Modules.Finance.Desktop\Pos.Modules.Finance.Desktop.csproj'
            )
            Files = @(
                @{ Source = 'src\Modules\Pos.Modules.Finance\bin\Release\net10.0\Pos.Modules.Finance.dll'; Package = 'Pos.Modules.Finance.dll' },
                @{ Source = 'src\Modules\Pos.Modules.Finance.Desktop\bin\Release\net10.0-windows\Pos.Modules.Finance.Desktop.dll'; Package = 'Pos.Modules.Finance.Desktop.dll' },
                @{ Source = 'src\Modules\Pos.Modules.Finance\migrations\finance-0001.json'; Package = 'migrations/finance-0001.json' },
                @{ Source = 'src\Modules\Pos.Modules.Finance\migrations\finance-0002.json'; Package = 'migrations/finance-0002.json' }
            )
        }
    }
    }
}

$publisherProject = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\Pos.Modules.Publisher.csproj'
$publicKeyPath = Join-Path $repoRoot 'security\module-publishers\pos-publisher-vps-2026.pem'
if (-not (Test-Path -LiteralPath $publicKeyPath -PathType Leaf)) {
    throw 'No se encontró la clave pública oficial del publicador en security\module-publishers.'
}

if (Test-Path -LiteralPath $outputRoot) {
    throw "La carpeta temporal ya existe y no se modificó: '$outputRoot'. Elige otro -OutputDirectory o mueve la carpeta existente manualmente."
}
$unsignedDirectory = Join-Path $outputRoot 'unsigned'
$publisherDirectory = Join-Path $outputRoot 'publisher'
$stageRoot = Join-Path $outputRoot 'stage'
New-Item -ItemType Directory -Path $unsignedDirectory,$publisherDirectory,$stageRoot -Force | Out-Null

foreach ($spec in $specs) {
    $manifestPath = Join-Path $repoRoot $spec.Manifest
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.id -cne $spec.Id -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
        throw "El manifiesto no coincide con el módulo seleccionado '$($spec.Id)'."
    }
    $version = [string]$manifest.version
    $stage = Join-Path $stageRoot $spec.Id
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $fileListPath = Join-Path $stage 'package-files.json'
    $fileList = [ordered]@{ files = @($spec.Files | ForEach-Object { [string]$_.Package }) } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($fileListPath, $fileList, [Text.UTF8Encoding]::new($false))

    foreach ($projectPath in $spec.BuildProjects) {
        & dotnet build (Join-Path $repoRoot $projectPath) --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Falló la compilación de $projectPath." }
    }
    foreach ($file in $spec.Files) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $file.Source) -PathType Leaf)) {
            throw "No se produjo el archivo requerido para $($spec.Id): $($file.Source)"
        }
        $relativeDestination = ([string]$file.Package).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $destination = Join-Path $stage $relativeDestination
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot $file.Source) -Destination $destination -Force
    }

    $publisherAssembly = Join-Path $repoRoot 'src\Modules\Pos.Modules.Publisher\bin\Release\net10.0\Pos.Modules.Publisher.dll'
    $unsignedPath = Join-Path $unsignedDirectory "$($spec.Id)-$version-unsigned.zip"
    & dotnet $publisherAssembly build-unsigned --manifest $manifestPath --stage $stage --files $fileListPath --output $unsignedPath --key-id $KeyId
    if ($LASTEXITCODE -ne 0) { throw "No se pudo preparar el paquete sin firma de $($spec.Id)." }
    Write-Output "Paquete sin firma preparado: $unsignedPath"
    Write-Output "Módulo: $($spec.Id); versión: $version; firmante esperado: $KeyId"
}

& dotnet build $publisherProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la herramienta publicadora.' }
& dotnet publish $publisherProject --configuration Release --runtime linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $publisherDirectory
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el firmador Linux autónomo.' }
Copy-Item -LiteralPath $publicKeyPath -Destination (Join-Path $outputRoot 'trusted-publisher.pem') -Force

Write-Output "Paquetes sin firma preparados: $($specs.Count)"
