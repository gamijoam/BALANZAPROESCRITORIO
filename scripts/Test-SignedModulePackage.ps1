[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$PublicKeyPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [IO.Path]::GetFullPath($PackagePath)
$keyPath = [IO.Path]::GetFullPath($PublicKeyPath)
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "No existe el paquete: $package" }
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) { throw "No existe la clave pública: $keyPath" }

$archive = [IO.Compression.ZipFile]::OpenRead($package)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        if ($entries.ContainsKey($entry.FullName)) { throw "Entrada repetida en ZIP: $($entry.FullName)" }
        $entries[$entry.FullName] = $entry
    }
    foreach ($required in @('module.manifest.json', 'package.index.json', 'package.signature')) {
        if (-not $entries.ContainsKey($required)) { throw "Falta $required en el ZIP." }
    }
    $indexStream = $entries['package.index.json'].Open()
    try { $memory = [IO.MemoryStream]::new(); $indexStream.CopyTo($memory); $indexBytes = $memory.ToArray() } finally { $indexStream.Dispose() }
    $index = [Text.Encoding]::UTF8.GetString($indexBytes) | ConvertFrom-Json
    $signatureStream = $entries['package.signature'].Open()
    try { $reader = [IO.StreamReader]::new($signatureStream, [Text.Encoding]::ASCII); $signatureText = $reader.ReadToEnd().Trim() } finally { $signatureStream.Dispose() }
    $signature = [Convert]::FromBase64String($signatureText)
    $rsa.ImportFromPem([IO.File]::ReadAllText($keyPath))
    if (-not $rsa.VerifyData($indexBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'La firma RSA-PSS del índice no es válida.'
    }
    foreach ($file in $index.files) {
        if (-not $entries.ContainsKey([string]$file.path)) { throw "El índice declara un archivo ausente: $($file.path)" }
        $stream = $entries[[string]$file.path].Open()
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        } finally { $stream.Dispose() }
        if ($hash -cne [string]$file.sha256) { throw "Hash no coincide: $($file.path)" }
    }
    $manifestStream = $entries['module.manifest.json'].Open()
    try { $manifestMemory = [IO.MemoryStream]::new(); $manifestStream.CopyTo($manifestMemory); $manifest = [Text.Encoding]::UTF8.GetString($manifestMemory.ToArray()) | ConvertFrom-Json } finally { $manifestStream.Dispose() }
    if ($manifest.desktopEntryAssembly -and -not $entries.ContainsKey([string]$manifest.desktopEntryAssembly)) {
        throw "Falta el ensamblado de escritorio declarado: $($manifest.desktopEntryAssembly)"
    }
    if ($manifest.entryAssembly -and -not $entries.ContainsKey([string]$manifest.entryAssembly)) {
        throw "Falta el ensamblado del servidor declarado: $($manifest.entryAssembly)"
    }
    Write-Output "Paquete verificado: $($manifest.id) $($manifest.version); firmante $($index.signerKeyId); archivos firmados $($index.files.Count)."
}
finally {
    $rsa.Dispose()
    $archive.Dispose()
}
