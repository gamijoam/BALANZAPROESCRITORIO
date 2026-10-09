[CmdletBinding()]
param([string]$BaseUrl = 'http://localhost:5155')

$ErrorActionPreference = 'Stop'
$uri = [Uri]$BaseUrl
if (-not $uri.IsLoopback) { throw 'La configuración inicial solo se permite mediante una dirección local.' }

$username = Read-Host 'Nombre del primer operador'
$securePin = Read-Host 'PIN del operador (4 a 12 dígitos)' -AsSecureString
$pinPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePin)
try {
    $pin = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pinPointer)
    $body = @{ username = $username; pin = $pin } | ConvertTo-Json -Compress
    $created = Invoke-RestMethod -Method Post -Uri "$($uri.AbsoluteUri.TrimEnd('/'))/api/v1/setup/first-operator" -ContentType 'application/json' -Body $body
    Write-Output 'Primer operador creado.'
} finally {
    $pin = $null
    $body = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pinPointer)
    $securePin.Dispose()
}
