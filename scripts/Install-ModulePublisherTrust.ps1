[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicKeyPath,
    [string]$KeyId = 'pos-publisher-vps-2026'
)

$ErrorActionPreference = 'Stop'
if ($KeyId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$') {
    throw 'El identificador de publicador no tiene un formato permitido.'
}
$resolvedPublicKey = (Resolve-Path -LiteralPath $PublicKeyPath -ErrorAction Stop).Path
$publicPem = [IO.File]::ReadAllText($resolvedPublicKey).Trim()
if (-not $publicPem.StartsWith('-----BEGIN PUBLIC KEY-----', [StringComparison]::Ordinal) -or
    -not $publicPem.EndsWith('-----END PUBLIC KEY-----', [StringComparison]::Ordinal) -or
    $publicPem.Contains('PRIVATE KEY', [StringComparison]::Ordinal)) {
    throw 'El archivo debe contener únicamente una clave pública PEM SPKI.'
}
try {
    $rsa = [Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($publicPem)
    if ($rsa.KeySize -lt 2048) { throw 'La clave RSA pública debe tener al menos 2048 bits.' }
} catch {
    throw "La clave pública PEM no es una clave RSA SPKI válida: $($_.Exception.Message)"
} finally {
    if ($null -ne $rsa) { $rsa.Dispose() }
}

$securityDirectory = Join-Path $env:LOCALAPPDATA 'PuntoDeVenta\Security'
$keyRingPath = Join-Path $securityDirectory 'TrustedModuleKeys.json'
New-Item -ItemType Directory -Path $securityDirectory -Force | Out-Null

if (Test-Path -LiteralPath $keyRingPath -PathType Leaf) {
    try { $keyRing = Get-Content -LiteralPath $keyRingPath -Raw | ConvertFrom-Json }
    catch { throw "El anillo de confianza actual no se pudo leer; no se modificó: $keyRingPath" }
    if ($keyRing.schemaVersion -ne 1 -or $null -eq $keyRing.keys) {
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
