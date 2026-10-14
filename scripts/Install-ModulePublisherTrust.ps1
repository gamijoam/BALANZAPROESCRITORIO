[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicKeyPath,
    [string]$KeyId = 'pos-publisher-vps-2026'
)

$ErrorActionPreference = 'Stop'

function Read-DerElement {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,
        [Parameter(Mandatory = $true)]
        [int]$Offset
    )

    if ($Offset -lt 0 -or $Offset -ge $Bytes.Length) {
        throw 'La clave pública está truncada.'
    }

    $tag = [int]$Bytes[$Offset]
    $position = $Offset + 1
    if ($position -ge $Bytes.Length) { throw 'La clave pública está truncada.' }

    $firstLength = [int]$Bytes[$position]
    $position++
    if (($firstLength -band 0x80) -eq 0) {
        $length = $firstLength
    } else {
        $lengthBytes = $firstLength -band 0x7f
        if ($lengthBytes -lt 1 -or $lengthBytes -gt 4 -or ($position + $lengthBytes) -gt $Bytes.Length) {
            throw 'La longitud DER de la clave pública no es válida.'
        }
        $length = 0
        for ($i = 0; $i -lt $lengthBytes; $i++) {
            $length = ($length -shl 8) -bor [int]$Bytes[$position]
            $position++
        }
    }

    if ($length -lt 0 -or ($position + $length) -gt $Bytes.Length) {
        throw 'La clave pública está truncada.'
    }

    return [pscustomobject]@{
        Tag = $tag
        ValueOffset = $position
        Length = $length
        NextOffset = ($position + $length)
    }
}

function Get-DerIntegerBytes {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes,
        [Parameter(Mandatory = $true)]
        $Element
    )

    if ($Element.Tag -ne 0x02 -or $Element.Length -lt 1) {
        throw 'La clave RSA pública contiene un entero DER inválido.'
    }
    if (($Bytes[$Element.ValueOffset] -band 0x80) -ne 0) {
        throw 'La clave RSA pública contiene un entero negativo.'
    }

    $start = $Element.ValueOffset
    $length = $Element.Length
    while ($length -gt 1 -and $Bytes[$start] -eq 0) {
        $start++
        $length--
    }

    $value = [byte[]]::new($length)
    [Array]::Copy($Bytes, $start, $value, 0, $length)
    return ,$value
}

function ConvertFrom-RsaPublicKeyPem {
    param([Parameter(Mandatory = $true)][string]$Pem)

    $header = '-----BEGIN PUBLIC KEY-----'
    $footer = '-----END PUBLIC KEY-----'
    $base64 = $Pem.Substring($header.Length, $Pem.Length - $header.Length - $footer.Length)
    $der = [Convert]::FromBase64String(($base64 -replace '\s', ''))

    $subjectPublicKeyInfo = Read-DerElement -Bytes $der -Offset 0
    if ($subjectPublicKeyInfo.Tag -ne 0x30 -or $subjectPublicKeyInfo.NextOffset -ne $der.Length) {
        throw 'El archivo no contiene una estructura DER SubjectPublicKeyInfo válida.'
    }

    $algorithm = Read-DerElement -Bytes $der -Offset $subjectPublicKeyInfo.ValueOffset
    if ($algorithm.Tag -ne 0x30) { throw 'El algoritmo de la clave pública no es válido.' }
    $oid = Read-DerElement -Bytes $der -Offset $algorithm.ValueOffset
    $rsaOid = [byte[]](0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01)
    if ($oid.Tag -ne 0x06 -or $oid.Length -ne $rsaOid.Length) {
        throw 'La clave pública no usa el algoritmo RSA.'
    }
    for ($i = 0; $i -lt $rsaOid.Length; $i++) {
        if ($der[$oid.ValueOffset + $i] -ne $rsaOid[$i]) {
            throw 'La clave pública no usa el algoritmo RSA.'
        }
    }

    $bitString = Read-DerElement -Bytes $der -Offset $algorithm.NextOffset
    if ($bitString.Tag -ne 0x03 -or $bitString.Length -lt 2 -or
        $bitString.NextOffset -ne $subjectPublicKeyInfo.NextOffset -or
        $der[$bitString.ValueOffset] -ne 0) {
        throw 'La clave pública RSA no contiene un bloque BIT STRING válido.'
    }

    $rsaSequence = Read-DerElement -Bytes $der -Offset ($bitString.ValueOffset + 1)
    if ($rsaSequence.Tag -ne 0x30 -or $rsaSequence.NextOffset -ne $bitString.NextOffset) {
        throw 'La estructura interna de la clave RSA no es válida.'
    }
    $modulusElement = Read-DerElement -Bytes $der -Offset $rsaSequence.ValueOffset
    $exponentElement = Read-DerElement -Bytes $der -Offset $modulusElement.NextOffset
    if ($exponentElement.NextOffset -ne $rsaSequence.NextOffset) {
        throw 'La estructura interna de la clave RSA tiene datos inesperados.'
    }

    $parameters = New-Object Security.Cryptography.RSAParameters
    $parameters.Modulus = Get-DerIntegerBytes -Bytes $der -Element $modulusElement
    $parameters.Exponent = Get-DerIntegerBytes -Bytes $der -Element $exponentElement
    return $parameters
}

if ($KeyId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$') {
    throw 'El identificador de publicador no tiene un formato permitido.'
}
$resolvedPublicKey = (Resolve-Path -LiteralPath $PublicKeyPath -ErrorAction Stop).Path
$publicPem = [IO.File]::ReadAllText($resolvedPublicKey).Trim()
if (-not $publicPem.StartsWith('-----BEGIN PUBLIC KEY-----', [StringComparison]::Ordinal) -or
    -not $publicPem.EndsWith('-----END PUBLIC KEY-----', [StringComparison]::Ordinal) -or
    $publicPem.IndexOf('PRIVATE KEY', [StringComparison]::Ordinal) -ge 0) {
    throw 'El archivo debe contener únicamente una clave pública PEM SPKI.'
}
$rsa = $null
try {
    $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
    $rsaParameters = ConvertFrom-RsaPublicKeyPem -Pem $publicPem
    $rsa.ImportParameters($rsaParameters) | Out-Null
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
