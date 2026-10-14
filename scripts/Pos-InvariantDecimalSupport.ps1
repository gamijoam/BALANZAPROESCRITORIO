function Convert-PosDecimalValue {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Context
    )

    $culture = [Globalization.CultureInfo]::InvariantCulture
    $text = [Convert]::ToString($Value, $culture)
    $parsed = [decimal]0
    if ([string]::IsNullOrWhiteSpace($text) -or
        -not [decimal]::TryParse($text, [Globalization.NumberStyles]::Float, $culture, [ref]$parsed)) {
        throw "El importe recibido para $Context no tiene un formato decimal válido."
    }
    return $parsed
}
