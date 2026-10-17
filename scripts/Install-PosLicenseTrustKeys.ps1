[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicKeyRingPath,
    [string[]]$DestinationDirectory = @(),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

function Read-LicensePublicKeyRing {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $file = Get-Item -LiteralPath $resolvedPath
    if ($file.Length -le 0 -or $file.Length -gt 1MB) {
        throw "El anillo de llaves públicas de licencias está vacío o excede 1 MB: '$resolvedPath'."
    }

    try { $document = [IO.File]::ReadAllText($resolvedPath) | ConvertFrom-Json }
    catch { throw "No se pudo leer el anillo de llaves públicas de licencias '$resolvedPath': $($_.Exception.Message)" }
    if ($null -eq $document -or [int]$document.schemaVersion -ne 1) {
        throw "El anillo de llaves públicas de licencias '$resolvedPath' tiene una versión incompatible."
    }

    $keys = @($document.keys)
    if ($keys.Count -eq 0) { throw "El anillo de llaves públicas de licencias '$resolvedPath' no contiene llaves." }
    $keyMap = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $keys) {
        $keyId = [string]$key.keyId
        $publicKeyPem = [string]$key.publicKeyPem
        if ($keyId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$' -or
            [string]::IsNullOrWhiteSpace($publicKeyPem) -or
            $publicKeyPem -notmatch '-----BEGIN PUBLIC KEY-----[\s\S]+-----END PUBLIC KEY-----') {
            throw "El anillo '$resolvedPath' contiene una llave incompleta o con formato inválido."
        }
        if ($keyMap.ContainsKey($keyId)) {
            throw "El anillo '$resolvedPath' contiene el identificador de llave repetido '$keyId'."
        }
        $keyMap.Add($keyId, [pscustomobject]@{ keyId = $keyId; publicKeyPem = $publicKeyPem })
    }
    return ,$keyMap
}

function Get-NormalizedPem {
    param([Parameter(Mandatory)][string]$Pem)
    return ($Pem -replace '\s', '')
}

$sourceKeys = Read-LicensePublicKeyRing -Path $PublicKeyRingPath
if ($ValidateOnly) {
    Write-Output 'El anillo de llaves públicas de licencias tiene un formato válido.'
    return
}
if ($DestinationDirectory.Count -eq 0) {
    throw 'Indica al menos una carpeta de destino para instalar el anillo de llaves públicas.'
}

foreach ($directory in $DestinationDirectory) {
    if ([string]::IsNullOrWhiteSpace($directory) -or -not [IO.Path]::IsPathFullyQualified($directory)) {
        throw "La carpeta de destino debe ser una ruta absoluta: '$directory'."
    }
    $resolvedDirectory = [IO.Path]::GetFullPath($directory)
    $destinationPath = Join-Path $resolvedDirectory 'trusted-license-keys.json'
    $mergedKeys = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)

    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        $existingKeys = Read-LicensePublicKeyRing -Path $destinationPath
        foreach ($existingKey in $existingKeys.Values) { $mergedKeys.Add($existingKey.keyId, $existingKey) }
    }

    foreach ($sourceKey in $sourceKeys.Values) {
        if ($mergedKeys.ContainsKey($sourceKey.keyId)) {
            $existingKey = $mergedKeys[$sourceKey.keyId]
            if ((Get-NormalizedPem -Pem $existingKey.publicKeyPem) -cne (Get-NormalizedPem -Pem $sourceKey.publicKeyPem)) {
                throw "El identificador de llave '$($sourceKey.keyId)' ya está asociado a otra clave en '$destinationPath'. Asigna un ID nuevo al rotar una llave."
            }
            continue
        }
        $mergedKeys.Add($sourceKey.keyId, $sourceKey)
    }

    $outputDocument = [ordered]@{
        schemaVersion = 1
        keys = @($mergedKeys.Values | Sort-Object keyId | ForEach-Object {
            [ordered]@{ keyId = $_.keyId; publicKeyPem = $_.publicKeyPem }
        })
    }
    $json = ConvertTo-Json -InputObject $outputDocument -Depth 8
    New-Item -ItemType Directory -Path $resolvedDirectory -Force | Out-Null
    $temporaryPath = "$destinationPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $destinationPath, [NullString]::Value)
        } else {
            [IO.File]::Move($temporaryPath, $destinationPath)
        }
    } finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
    Write-Output "Llaves públicas de licencias preparadas en '$destinationPath'."
}
