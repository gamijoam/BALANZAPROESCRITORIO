function ConvertFrom-PosJsonRows {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Json)

    # Windows PowerShell 5.1 can preserve a JSON root array as one object when
    # an array-returning expression is wrapped in another @(...). Enumerate
    # rows explicitly so callers always receive one pipeline item per row.
    $parsed = ConvertFrom-Json -InputObject $Json
    foreach ($row in $parsed) {
        Write-Output $row
    }
}
