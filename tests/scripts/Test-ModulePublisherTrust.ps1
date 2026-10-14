[CmdletBinding()]
param([string]$InstallerPath)

$ErrorActionPreference = 'Stop'
$installer = if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    Join-Path $PSScriptRoot '..\..\scripts\Install-ModulePublisherTrust.ps1'
} else {
    $InstallerPath
}
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'RED: todavía no existe la instalación aditiva de confianza para el nuevo publicador.'
}

function New-DerElement {
    param([Parameter(Mandatory)][byte]$Tag, [Parameter(Mandatory)][byte[]]$Payload)

    $encoded = [System.Collections.Generic.List[byte]]::new()
    $encoded.Add($Tag)
    if ($Payload.Length -lt 128) {
        $encoded.Add([byte]$Payload.Length)
    } else {
        $lengthBytes = [System.Collections.Generic.List[byte]]::new()
        $remaining = $Payload.Length
        while ($remaining -gt 0) {
            $lengthBytes.Insert(0, [byte]($remaining -band 0xff))
            $remaining = $remaining -shr 8
        }
        $encoded.Add([byte](0x80 -bor $lengthBytes.Count))
        $encoded.AddRange($lengthBytes)
    }
    $encoded.AddRange($Payload)
    return ,$encoded.ToArray()
}

function ConvertTo-DerInteger {
    param([Parameter(Mandatory)][byte[]]$Value)

    $first = 0
    while ($first -lt $Value.Length - 1 -and $Value[$first] -eq 0) { $first++ }
    $integerBytes = [byte[]]$Value[$first..($Value.Length - 1)]
    if (($integerBytes[0] -band 0x80) -ne 0) { $integerBytes = [byte[]](@([byte]0) + $integerBytes) }
    return ,(New-DerElement -Tag ([byte]0x02) -Payload $integerBytes)
}

function Export-RsaPublicKeyPem {
    param([Parameter(Mandatory)][Security.Cryptography.RSA]$Key)

    $parameters = $Key.ExportParameters($false)
    $modulus = ConvertTo-DerInteger -Value $parameters.Modulus
    $exponent = ConvertTo-DerInteger -Value $parameters.Exponent
    $rsaKey = New-DerElement -Tag ([byte]0x30) -Payload ([byte[]]($modulus + $exponent))
    $algorithm = [byte[]](0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00)
    $bitString = New-DerElement -Tag ([byte]0x03) -Payload ([byte[]](@([byte]0) + $rsaKey))
    $subjectPublicKeyInfo = New-DerElement -Tag ([byte]0x30) -Payload ([byte[]]($algorithm + $bitString))
    $base64 = [Convert]::ToBase64String($subjectPublicKeyInfo)
    $lines = for ($offset = 0; $offset -lt $base64.Length; $offset += 64) {
        $length = [Math]::Min(64, $base64.Length - $offset)
        $base64.Substring($offset, $length)
    }
    return "-----BEGIN PUBLIC KEY-----`r`n$($lines -join "`r`n")`r`n-----END PUBLIC KEY-----`r`n"
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("pos-trust-test-" + [guid]::NewGuid().ToString('N'))
$previousLocalAppData = $env:LOCALAPPDATA
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $env:LOCALAPPDATA = $root
    $securityDirectory = Join-Path $root 'PuntoDeVenta\Security'
    New-Item -ItemType Directory -Path $securityDirectory -Force | Out-Null
    $ringPath = Join-Path $securityDirectory 'TrustedModuleKeys.json'
    $oldKey = [Security.Cryptography.RSA]::Create(2048)
    $newKey = [Security.Cryptography.RSA]::Create(2048)
    $otherKey = [Security.Cryptography.RSA]::Create(2048)
    try {
        $oldPem = Export-RsaPublicKeyPem -Key $oldKey
        $newPem = Export-RsaPublicKeyPem -Key $newKey
        $otherPem = Export-RsaPublicKeyPem -Key $otherKey
        [IO.File]::WriteAllText((Join-Path $root 'new.pem'), $newPem, [Text.Encoding]::ASCII)
        [IO.File]::WriteAllText((Join-Path $root 'other.pem'), $otherPem, [Text.Encoding]::ASCII)
        $initialRing = [pscustomobject]@{
            schemaVersion = 1
            keys = @([pscustomobject]@{ keyId = 'legacy-publisher'; publicKeyPem = $oldPem; status = 'Active' })
        } | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($ringPath, $initialRing, [Text.UTF8Encoding]::new($false))

        & $installer -PublicKeyPath (Join-Path $root 'new.pem') -KeyId 'pos-publisher-vps-2026'
        $ring = Get-Content -LiteralPath $ringPath -Raw | ConvertFrom-Json
        if (@($ring.keys).Count -ne 2) { throw 'La clave nueva debe agregarse sin quitar la clave anterior.' }
        if (($ring.keys | Where-Object keyId -eq 'legacy-publisher').publicKeyPem -cne $oldPem) {
            throw 'La clave anterior se modificó durante la incorporación.'
        }
        $installedPublicKeyPem = [string](($ring.keys | Where-Object keyId -eq 'pos-publisher-vps-2026').publicKeyPem)
        $installedPublicKeyBody = $installedPublicKeyPem -replace '-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s', ''
        $expectedPublicKeyBody = $newPem -replace '-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s', ''
        if ($installedPublicKeyBody -cne $expectedPublicKeyBody) {
            throw 'No se agregó la clave pública esperada.'
        }

        $newKeyEntry = $ring.keys | Where-Object keyId -eq 'pos-publisher-vps-2026'
        $encodedNewKey = ($newPem -replace '-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s', '')
        $wrappedLines = for ($offset = 0; $offset -lt $encodedNewKey.Length; $offset += 40) {
            $length = [Math]::Min(40, $encodedNewKey.Length - $offset)
            $encodedNewKey.Substring($offset, $length)
        }
        $reformattedPem = "-----BEGIN PUBLIC KEY-----`r`n$($wrappedLines -join "`r`n")`r`n-----END PUBLIC KEY-----`r`n"
        $newKeyEntry.publicKeyPem = $reformattedPem
        $reformattedRing = [pscustomobject]@{ schemaVersion = 1; keys = @($ring.keys) } | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($ringPath, $reformattedRing, [Text.UTF8Encoding]::new($false))

        $equivalentKeyAccepted = $false
        try {
            & $installer -PublicKeyPath (Join-Path $root 'new.pem') -KeyId 'pos-publisher-vps-2026'
            $equivalentKeyAccepted = $true
        } catch {
            throw "El instalador debe aceptar la misma clave SPKI con otros saltos de línea: $($_.Exception.Message)"
        }
        if (-not $equivalentKeyAccepted) { throw 'La misma clave SPKI con otro formato PEM debe aceptarse.' }
        $ring = Get-Content -LiteralPath $ringPath -Raw | ConvertFrom-Json
        if (@($ring.keys).Count -ne 2) { throw 'El formato alternativo no debe duplicar la clave de confianza.' }

        $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf) {
            & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $installer -PublicKeyPath (Join-Path $root 'new.pem') -KeyId 'pos-publisher-vps-2026'
            if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell 5.1 debe aceptar la misma clave con otro formato PEM.' }
            $ring = Get-Content -LiteralPath $ringPath -Raw | ConvertFrom-Json
            if (@($ring.keys).Count -ne 2) { throw 'Windows PowerShell 5.1 duplicó la clave equivalente.' }
        }

        $collisionRejected = $false
        try { & $installer -PublicKeyPath (Join-Path $root 'other.pem') -KeyId 'pos-publisher-vps-2026' }
        catch { $collisionRejected = $_.Exception.Message.IndexOf('ya existe', [StringComparison]::OrdinalIgnoreCase) -ge 0 }
        if (-not $collisionRejected) { throw 'Un ID existente asociado a otra clave debe rechazarse.' }
        $afterCollision = Get-Content -LiteralPath $ringPath -Raw | ConvertFrom-Json
        if (@($afterCollision.keys).Count -ne 2 -or
            ($afterCollision.keys | Where-Object keyId -eq 'pos-publisher-vps-2026').publicKeyPem -cne $reformattedPem) {
            throw 'El anillo cambió después de rechazar una colisión.'
        }
    } finally {
        $oldKey.Dispose()
        $newKey.Dispose()
        $otherKey.Dispose()
    }
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'OK: la clave nueva se agrega sin quitar otras y las colisiones se rechazan.'
