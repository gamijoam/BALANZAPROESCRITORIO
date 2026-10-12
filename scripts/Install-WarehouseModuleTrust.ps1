[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicKeyPath,
    [string]$KeyId = 'pos-warehouses-dev-2026'
)

$ErrorActionPreference = 'Stop'
$resolvedPublicKey = (Resolve-Path -LiteralPath $PublicKeyPath).Path
$publicPem = [IO.File]::ReadAllText($resolvedPublicKey).Trim()
if (-not $publicPem.StartsWith('-----BEGIN PUBLIC KEY-----', [StringComparison]::Ordinal) -or
    $publicPem.IndexOf('-----END PUBLIC KEY-----', [StringComparison]::Ordinal) -lt 0 -or
    $publicPem.IndexOf('PRIVATE KEY', [StringComparison]::Ordinal) -ge 0) {
    throw 'El archivo debe contener únicamente una clave pública PEM SPKI.'
}
$publicKeyBody = $publicPem -replace '-----BEGIN PUBLIC KEY-----', '' -replace '-----END PUBLIC KEY-----', '' -replace '\s', ''
try { $publicKeyDer = [Convert]::FromBase64String($publicKeyBody) }
catch { throw 'El contenido de la clave pública PEM no es Base64 válido.' }
if ($publicKeyDer.Length -lt 128) { throw 'La clave pública PEM es demasiado corta.' }

$securityDirectory = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Security'
$keyRingPath = Join-Path $securityDirectory 'TrustedModuleKeys.json'
New-Item -ItemType Directory -Path $securityDirectory -Force | Out-Null

if (Test-Path -LiteralPath $keyRingPath) {
    $keyRing = Get-Content -LiteralPath $keyRingPath -Raw | ConvertFrom-Json
    if ($keyRing.schemaVersion -ne 1 -or -not $keyRing.keys) {
        throw "El anillo de confianza actual no tiene el formato esperado; no se modificó: $keyRingPath"
    }
    $matches = @($keyRing.keys | Where-Object { $_.keyId -ceq $KeyId })
    if ($matches.Count -gt 0) {
        if ($matches[0].publicKeyPem.Trim() -cne $publicPem) {
            throw "Ya existe '$KeyId' con otra clave. No se sobrescribió el anillo de confianza."
        }
    } else {
        $keyRing.keys = @($keyRing.keys) + @([pscustomobject]@{
            keyId = $KeyId
            publicKeyPem = $publicPem
            status = 'Active'
        })
    }
} else {
    $keyRing = [pscustomobject]@{
        schemaVersion = 1
        keys = @([pscustomobject]@{
            keyId = $KeyId
            publicKeyPem = $publicPem
            status = 'Active'
        })
    }
}

$temporaryPath = "$keyRingPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $json = $keyRing | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $keyRingPath -Force
} finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
}

Write-Output "Clave pública '$KeyId' lista en el anillo externo de confianza: $keyRingPath"
