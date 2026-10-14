[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1')
. (Join-Path $PSScriptRoot 'Pos-InvariantDecimalSupport.ps1')
. (Join-Path $PSScriptRoot 'Pos-JsonArraySupport.ps1')

function Invoke-PosApiRequest {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [string]$AccessToken
    )

    $uri = [uri]::new([uri]$script:apiBaseUrl, $Path)
    $request = [System.Net.HttpWebRequest]::Create($uri)
    $request.Method = $Method
    $request.Timeout = 20000
    $request.ReadWriteTimeout = 20000
    $request.ServerCertificateValidationCallback = [PosPinnedCertificate]::CreateCallback($script:certificatePin)
    if (-not [string]::IsNullOrWhiteSpace($script:terminalCredential)) {
        $request.Headers['X-POS-Terminal-Credential'] = $script:terminalCredential
    }
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $request.Headers['Authorization'] = "Bearer $AccessToken"
    }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json; charset=utf-8'
        $bodyBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Body -Compress -Depth 10))
        $request.ContentLength = $bodyBytes.Length
        $stream = $request.GetRequestStream()
        try { $stream.Write($bodyBytes, 0, $bodyBytes.Length) } finally { $stream.Dispose() }
    }

    $response = $null
    $statusCode = 0
    $responseText = ''
    try {
        $response = $request.GetResponse()
        $statusCode = [int]$response.StatusCode
    } catch [Net.WebException] {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw 'No se recibió respuesta del servicio POS.' }
        $statusCode = [int]$response.StatusCode
    }
    if ($null -ne $response) {
        $reader = New-Object IO.StreamReader($response.GetResponseStream())
        try { $responseText = $reader.ReadToEnd() } finally { $reader.Dispose(); $response.Close() }
    }

    $responseData = $null
    if (-not [string]::IsNullOrWhiteSpace($responseText)) {
        try { $responseData = $responseText | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{ StatusCode = $statusCode; Data = $responseData }
}

function Get-LegacyDebtRows {
    param([Parameter(Mandatory)]$DatabaseSettings)

    $psql = Join-Path (Get-PostgreSqlBinaryDirectory) 'psql.exe'
    $sql = @'
WITH debt AS (
    SELECT c.id::text AS source_id, c.name AS source_name, c.id_number, c.phone,
           SUM(s.balance_pending)::numeric AS amount, COUNT(*)::int AS sale_count
    FROM novedades_stage.customers c
    JOIN novedades_stage.sales s ON s.customer_id = c.id
    WHERE s.is_credit IS TRUE AND COALESCE(s.balance_pending, 0) > 0
    GROUP BY c.id, c.name, c.id_number, c.phone
), candidates AS (
    SELECT d.*,
        (SELECT p."Id" FROM public.customers p
         WHERE p."Active" = TRUE
           AND regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') <> ''
           AND regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.id_number, ''), '\D', '', 'g')
         ORDER BY p."CreatedAtUtc", p."Id" LIMIT 1) AS doc_dest,
        (SELECT COUNT(*)::int FROM public.customers p
         WHERE p."Active" = TRUE
           AND regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') <> ''
           AND regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.id_number, ''), '\D', '', 'g')) AS doc_count,
        (SELECT p."Id" FROM public.customers p
         WHERE p."Active" = TRUE
           AND lower(trim(p."Name")) = lower(trim(d.source_name))
           AND regexp_replace(COALESCE(p."Phone", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.phone, ''), '\D', '', 'g')
           AND regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.id_number, ''), '\D', '', 'g')
           AND regexp_replace(COALESCE(d.id_number, ''), '\D', '', 'g') <> ''
         ORDER BY p."CreatedAtUtc", p."Id" LIMIT 1) AS doc_name_phone_dest,
        (SELECT COUNT(*)::int FROM public.customers p
         WHERE p."Active" = TRUE
           AND lower(trim(p."Name")) = lower(trim(d.source_name))
           AND regexp_replace(COALESCE(p."Phone", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.phone, ''), '\D', '', 'g')
           AND regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.id_number, ''), '\D', '', 'g')
           AND regexp_replace(COALESCE(d.id_number, ''), '\D', '', 'g') <> '') AS doc_name_phone_count,
        (SELECT p."Id" FROM public.customers p
         WHERE p."Active" = TRUE
           AND lower(trim(p."Name")) = lower(trim(d.source_name))
           AND regexp_replace(COALESCE(p."Phone", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.phone, ''), '\D', '', 'g')
         ORDER BY p."CreatedAtUtc", p."Id" LIMIT 1) AS name_phone_dest,
        (SELECT COUNT(*)::int FROM public.customers p
         WHERE p."Active" = TRUE
           AND lower(trim(p."Name")) = lower(trim(d.source_name))
           AND regexp_replace(COALESCE(p."Phone", ''), '\D', '', 'g') = regexp_replace(COALESCE(d.phone, ''), '\D', '', 'g')) AS name_phone_count,
        (SELECT p."Id" FROM public.customers p
         WHERE p."Active" = TRUE
           AND lower(trim(p."Name")) = lower(trim(d.source_name))
         ORDER BY p."CreatedAtUtc", p."Id" LIMIT 1) AS name_only_dest,
        (SELECT COUNT(*)::int FROM public.customers p
         WHERE p."Active" = TRUE
           AND lower(trim(p."Name")) = lower(trim(d.source_name))) AS name_count
    FROM debt d
), resolved AS (
    SELECT *,
        CASE WHEN doc_count = 1 THEN doc_dest
             WHEN doc_count > 1 AND doc_name_phone_count = 1 THEN doc_name_phone_dest
             WHEN doc_count = 0 AND name_phone_count = 1 AND regexp_replace(COALESCE(phone, ''), '\D', '', 'g') <> '' THEN name_phone_dest
             WHEN doc_count = 0 AND name_count = 1 AND regexp_replace(COALESCE(phone, ''), '\D', '', 'g') = '' THEN name_only_dest
             ELSE NULL END AS destination_id,
        CASE WHEN doc_count = 1 THEN 'documento'
             WHEN doc_count > 1 AND doc_name_phone_count = 1 AND regexp_replace(COALESCE(phone, ''), '\D', '', 'g') = '' THEN 'documento_duplicado_resuelto_por_nombre'
             WHEN doc_count > 1 AND doc_name_phone_count = 1 THEN 'documento_nombre_telefono'
             WHEN doc_count = 0 AND name_count = 1 AND regexp_replace(COALESCE(phone, ''), '\D', '', 'g') = '' THEN 'nombre_sin_documento_ni_telefono'
             WHEN doc_count = 0 AND name_phone_count = 1 AND regexp_replace(COALESCE(phone, ''), '\D', '', 'g') <> '' THEN 'nombre_telefono'
             ELSE 'sin_coincidencia_unica' END AS match_method
    FROM candidates
)
SELECT COALESCE(json_agg(json_build_object(
    'sourceId', r.source_id,
    'sourceName', r.source_name,
    'sourcePhoneLast4', right(regexp_replace(COALESCE(r.phone, ''), '\D', '', 'g'), 4),
    'sourceHasDocument', regexp_replace(COALESCE(r.id_number, ''), '\D', '', 'g') <> '',
    'sourceHasPhone', regexp_replace(COALESCE(r.phone, ''), '\D', '', 'g') <> '',
    'customerId', r.destination_id::text,
    'customerName', p."Name",
    'customerPhoneLast4', right(regexp_replace(COALESCE(p."Phone", ''), '\D', '', 'g'), 4),
    'customerHasDocument', regexp_replace(COALESCE(p."DocumentNumber", ''), '\D', '', 'g') <> '',
    'customerHasPhone', regexp_replace(COALESCE(p."Phone", ''), '\D', '', 'g') <> '',
    'amount', r.amount::text,
    'sales', r.sale_count,
    'method', r.match_method,
    'matchCount', CASE WHEN r.doc_count = 1 THEN r.doc_count
                       WHEN r.doc_count > 1 THEN r.doc_name_phone_count
                       WHEN regexp_replace(COALESCE(r.phone, ''), '\D', '', 'g') = '' THEN r.name_count
                       ELSE r.name_phone_count END,
    'sameNameCount', r.name_count,
    'active', p."Active",
    'currency', p."Currency",
    'existingEntries', (SELECT COUNT(*)::int FROM public.receivable_entries e WHERE e."CustomerId" = r.destination_id),
    'openingCount', (SELECT COUNT(*)::int FROM public.receivable_entries e WHERE e."CustomerId" = r.destination_id AND e."IsOpeningBalance" = TRUE),
    'openingAmount', (SELECT e."Amount"::text FROM public.receivable_entries e WHERE e."CustomerId" = r.destination_id AND e."IsOpeningBalance" = TRUE ORDER BY e."OccurredAtUtc" LIMIT 1),
    'openingKey', (SELECT e."IdempotencyKey" FROM public.receivable_entries e WHERE e."CustomerId" = r.destination_id AND e."IsOpeningBalance" = TRUE ORDER BY e."OccurredAtUtc" LIMIT 1),
    'openingReason', (SELECT e."Reason" FROM public.receivable_entries e WHERE e."CustomerId" = r.destination_id AND e."IsOpeningBalance" = TRUE ORDER BY e."OccurredAtUtc" LIMIT 1),
    'openingType', (SELECT e."EntryType" FROM public.receivable_entries e WHERE e."CustomerId" = r.destination_id AND e."IsOpeningBalance" = TRUE ORDER BY e."OccurredAtUtc" LIMIT 1)
) ORDER BY r.source_id), '[]'::json)::text
FROM resolved r
LEFT JOIN public.customers p ON p."Id" = r.destination_id;
'@

    $queryFile = Join-Path ([IO.Path]::GetTempPath()) "pos-legacy-debt-$([Guid]::NewGuid().ToString('N')).sql"
    $previousPgPassword = $env:PGPASSWORD
    $previousPgClientEncoding = $env:PGCLIENTENCODING
    $env:PGPASSWORD = $DatabaseSettings.Password
    $env:PGCLIENTENCODING = 'UTF8'
    try {
        [IO.File]::WriteAllText($queryFile, $sql, [Text.UTF8Encoding]::new($false))
        $output = & $psql --no-psqlrc --no-password --tuples-only --no-align `
            --set=ON_ERROR_STOP=1 --host $DatabaseSettings.Host --port ([string]$DatabaseSettings.Port) `
            --username $DatabaseSettings.Username --dbname $DatabaseSettings.Database --file $queryFile
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw 'Falló la consulta de solo lectura al origen legado.' }
        $json = ($output -join "`n").Trim()
        if ([string]::IsNullOrWhiteSpace($json)) { return @() }
        return @(ConvertFrom-PosJsonRows -Json $json)
    } finally {
        Remove-Item -LiteralPath $queryFile -Force -ErrorAction SilentlyContinue
        if ($null -eq $previousPgPassword) { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue } else { $env:PGPASSWORD = $previousPgPassword }
        if ($null -eq $previousPgClientEncoding) { Remove-Item Env:PGCLIENTENCODING -ErrorAction SilentlyContinue } else { $env:PGCLIENTENCODING = $previousPgClientEncoding }
    }
}

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory)][Security.SecureString]$SecureString)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$databaseSettings = $null
$accessToken = $null
$pinText = $null
$pinSecure = $null
$imported = 0
$replayed = 0
$verified = 0
$apiBaseUrl = $null
$certificatePin = $null
$terminalCredential = $null
try {
    $profile = Get-PosInstallationProfile
    if ($null -eq $profile -or [string]$profile.Mode -ne 'server') {
        throw 'Esta instalación no está configurada para usar un servidor POS.'
    }
    $script:apiBaseUrl = [string]$profile.ApiBaseUrl
    $script:certificatePin = [string]$profile.ServerCertificateSha256
    if ([string]::IsNullOrWhiteSpace($script:certificatePin)) { $script:certificatePin = [string]$profile.CertificateSha256 }
    if ([string]::IsNullOrWhiteSpace($script:certificatePin)) { throw 'El perfil no contiene la huella TLS del servidor POS.' }
    $script:terminalCredential = [string]$profile.TerminalCredential

    $health = Invoke-PosApiRequest -Method GET -Path 'health/live'
    if ($health.StatusCode -ne 200 -or $health.Data.status -ne 'ok') { throw 'El servidor POS no está disponible o no coincide el certificado.' }

    $databaseSettings = Get-PosDatabaseSettings
    $rows = @(Get-LegacyDebtRows -DatabaseSettings $databaseSettings)
    $total = [decimal]0
    foreach ($row in $rows) { $total += (Convert-PosDecimalValue -Value $row.amount -Context "saldo del origen $($row.sourceId)") }
    $mapped = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.customerId) })
    $unresolved = @($rows | Where-Object { [string]$_.method -eq 'sin_coincidencia_unica' -or [int]$_.matchCount -ne 1 })
    $ambiguousNameOnly = @($rows | Where-Object { [string]$_.method -eq 'nombre_sin_documento_ni_telefono' -and [int]$_.sameNameCount -ne 1 })
    $inactive = @($mapped | Where-Object { $_.active -ne $true -or [string]$_.currency -ne 'USD' })
    $invalidExisting = @()
    foreach ($row in $mapped) {
        if ([int]$row.existingEntries -eq 0) { continue }
        $expectedAmount = Convert-PosDecimalValue -Value $row.amount -Context "saldo esperado del origen $($row.sourceId)"
        $expectedKey = "legacy-novedades-tenant-2026-10-13:$($row.sourceId)"
        $expectedReason = "Saldo inicial migrado del sistema anterior; corte 13/10/2026; $([int]$row.sales) ventas a crédito."
        $existingAmount = [decimal]0
        $openingAmountText = [Convert]::ToString($row.openingAmount, [Globalization.CultureInfo]::InvariantCulture)
        $hasAmount = [decimal]::TryParse($openingAmountText, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$existingAmount)
        if ([int]$row.existingEntries -ne 1 -or [int]$row.openingCount -ne 1 -or
            -not $hasAmount -or $existingAmount -ne $expectedAmount -or
            [string]$row.openingKey -cne $expectedKey -or [string]$row.openingReason -cne $expectedReason -or
            [string]$row.openingType -cne 'Charge') {
            $invalidExisting += $row
        }
    }
    $duplicateDestinations = @($mapped.customerId | Group-Object | Where-Object Count -gt 1)

    Write-Host ''
    Write-Host 'REVISIÓN DE SALDOS PENDIENTES' -ForegroundColor Cyan
    Write-Host ("Clientes con deuda: {0}" -f $rows.Count)
    Write-Host ("Total pendiente: {0} USD" -f $total.ToString([Globalization.CultureInfo]::InvariantCulture))
    Write-Host ("Coincidencias únicas: {0}; sin resolver: {1}" -f $mapped.Count, $unresolved.Count)
    Write-Host ("Coincidencias solo por nombre con identidad ambigua: {0}" -f $ambiguousNameOnly.Count)
    Write-Host ("Aperturas previas compatibles para reintento: {0}; cuentas con movimientos distintos: {1}" -f (@($mapped | Where-Object { [int]$_.existingEntries -eq 1 }).Count - $invalidExisting.Count), $invalidExisting.Count)
    Write-Host ("Moneda inválida/inactiva: {0}; destinos repetidos: {1}" -f $inactive.Count, $duplicateDestinations.Count)

    $reviewRows = @($rows | Where-Object { [string]$_.method -ne 'documento' })
    if ($reviewRows.Count -gt 0) {
        Write-Host ''
        Write-Host 'Coincidencias resueltas por nombre/teléfono; revísalas:' -ForegroundColor Yellow
        $reviewRows | Select-Object sourceId, sourceName, customerName, sourcePhoneLast4, customerPhoneLast4, sourceHasDocument, customerHasDocument, sourceHasPhone, customerHasPhone, amount, method | Format-Table -AutoSize | Out-Host
        if (@($reviewRows | Where-Object { [string]$_.method -eq 'nombre_sin_documento_ni_telefono' }).Count -gt 0) {
            Write-Host 'Hay una coincidencia solo por nombre, sin documento ni teléfono. Revísala especialmente antes de confirmar.' -ForegroundColor Yellow
        }
    }

    $expectedTotal = Convert-PosDecimalValue -Value '1260.0513' -Context 'total esperado de la importación'
    if ($rows.Count -ne 81 -or $total -ne $expectedTotal -or $mapped.Count -ne 81 -or
        $unresolved.Count -ne 0 -or $ambiguousNameOnly.Count -ne 0 -or $inactive.Count -ne 0 -or $invalidExisting.Count -ne 0 -or
        $duplicateDestinations.Count -ne 0) {
        throw 'Los datos no coinciden con la carga revisada. No se registró ningún saldo.'
    }

    if (-not $Apply) {
        Write-Host ''
        Write-Host 'Vista previa solamente. Para registrar estos saldos, ejecuta de nuevo el archivo con -Apply.' -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host 'La carga registrará un saldo inicial por cliente mediante el servicio POS.' -ForegroundColor Yellow
    $confirmation = Read-Host 'Escribe CARGAR 81 para continuar'
    if ($confirmation -cne 'CARGAR 81') { Write-Host 'Cancelado. No se modificó ninguna cuenta.'; return }

    $terminalIdPath = Join-Path $env:LOCALAPPDATA 'POS\terminal-id.txt'
    if (-not (Test-Path -LiteralPath $terminalIdPath)) { throw 'No se encontró la identidad local de la caja POS.' }
    $terminalId = [guid]::Empty
    if (-not [guid]::TryParse((Get-Content -LiteralPath $terminalIdPath -Raw).Trim(), [ref]$terminalId)) { throw 'La identidad local de la caja POS no es válida.' }
    $desktopAssembly = Join-Path $env:LOCALAPPDATA 'Programs\Punto de Venta\desktop\Pos.Desktop.Presentation.dll'
    $clientVersion = if (Test-Path -LiteralPath $desktopAssembly) { [Reflection.AssemblyName]::GetAssemblyName($desktopAssembly).Version.ToString(3) } else { '1.0.0' }

    $username = Read-Host 'Usuario del POS'
    $pinSecure = Read-Host 'PIN del POS' -AsSecureString
    $pinText = Convert-SecureStringToPlainText -SecureString $pinSecure
    $login = Invoke-PosApiRequest -Method POST -Path 'api/v1/auth/login' -Body @{
        username = $username; pin = $pinText; terminalId = $terminalId; clientVersion = $clientVersion
    }
    $pinText = $null
    $pinSecure.Dispose()
    $pinSecure = $null
    if ($login.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace([string]$login.Data.accessToken)) {
        throw "No se pudo iniciar sesión en el servidor POS (HTTP $($login.StatusCode)). Revisa el usuario, el PIN y el permiso de cuentas por cobrar."
    }
    $accessToken = [string]$login.Data.accessToken

    $firstAccount = Invoke-PosApiRequest -Method GET -Path "api/v1/customers/$($rows[0].customerId)/receivables" -AccessToken $accessToken
    if ($firstAccount.StatusCode -ne 200) { throw "El operador no puede consultar cuentas por cobrar (HTTP $($firstAccount.StatusCode))." }
    $firstRow = $rows | Where-Object { [string]$_.customerId -eq $firstAccount.Data.id } | Select-Object -First 1
    if ($null -eq $firstRow) { throw 'La consulta del servicio no corresponde a la lista de origen.' }
    if (@($firstAccount.Data.entries).Count -ne [int]$firstRow.existingEntries) { throw 'La cuenta cambió desde la vista previa; vuelve a revisar antes de cargar.' }

    foreach ($row in $rows) {
        $amount = Convert-PosDecimalValue -Value $row.amount -Context "saldo que se registrará para el origen $($row.sourceId)"
        $reason = "Saldo inicial migrado del sistema anterior; corte 13/10/2026; $([int]$row.sales) ventas a crédito."
        $idempotencyKey = "legacy-novedades-tenant-2026-10-13:$($row.sourceId)"
        $requestBody = @{ amountUsd = $amount; reason = $reason; idempotencyKey = $idempotencyKey }
        $result = $null
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $result = Invoke-PosApiRequest -Method POST -Path "api/v1/customers/$($row.customerId)/receivables/opening-balance" -Body $requestBody -AccessToken $accessToken
                if ($result.StatusCode -ge 500 -and $attempt -lt 3) { Start-Sleep -Milliseconds 400; continue }
                break
            } catch {
                if ($attempt -ge 3) { throw }
                Start-Sleep -Milliseconds 400
            }
        }
        if ($result.StatusCode -eq 201) { $imported++ }
        elseif ($result.StatusCode -eq 200 -and $result.Data.replayed -eq $true) { $replayed++ }
        else {
            $code = if ($result.Data) { [string]$result.Data.error } else { '' }
            throw "El servicio rechazó el origen $($row.sourceId) (HTTP $($result.StatusCode), $code). Carga parcial: $imported nuevas, $replayed reintentadas."
        }
    }
    Write-Host ("El servicio confirmó {0} saldos nuevos y {1} reintentos idempotentes; verificando las cuentas..." -f $imported, $replayed) -ForegroundColor Green

    $verifiedTotal = [decimal]0
    foreach ($row in $rows) {
        $expected = Convert-PosDecimalValue -Value $row.amount -Context "saldo esperado al verificar el origen $($row.sourceId)"
        $account = Invoke-PosApiRequest -Method GET -Path "api/v1/customers/$($row.customerId)/receivables" -AccessToken $accessToken
        if ($account.StatusCode -ne 200) { throw "No se pudo verificar el origen $($row.sourceId) (HTTP $($account.StatusCode))." }
        $entries = @($account.Data.entries)
        if ($entries.Count -ne 1 -or $entries[0].isOpeningBalance -ne $true) { throw "El origen $($row.sourceId) no tiene exactamente un saldo inicial." }
        $actual = Convert-PosDecimalValue -Value $entries[0].amount -Context "saldo devuelto por el servicio para el origen $($row.sourceId)"
        $balance = Convert-PosDecimalValue -Value $account.Data.balance -Context "balance devuelto por el servicio para el origen $($row.sourceId)"
        if ($actual -ne $expected -or $balance -ne $expected) { throw "El saldo del origen $($row.sourceId) no coincide con el archivo legado." }
        $verified++
        $verifiedTotal += $actual
    }

    $auditSql = "SELECT COUNT(*) FROM public.audit_events WHERE `"Action`"='receivable.opening_balance.recorded' AND `"Payload`"::text LIKE '%legacy-novedades-tenant-2026-10-13:%';"
    $auditQueryFile = Join-Path ([IO.Path]::GetTempPath()) "pos-legacy-audit-$([Guid]::NewGuid().ToString('N')).sql"
    $previousPgPassword = $env:PGPASSWORD
    $previousPgClientEncoding = $env:PGCLIENTENCODING
    $env:PGPASSWORD = $databaseSettings.Password
    $env:PGCLIENTENCODING = 'UTF8'
    try {
        [IO.File]::WriteAllText($auditQueryFile, $auditSql, [Text.UTF8Encoding]::new($false))
        $auditOutput = & (Join-Path (Get-PostgreSqlBinaryDirectory) 'psql.exe') --no-psqlrc --no-password --tuples-only --no-align `
            --set=ON_ERROR_STOP=1 `
            --host $databaseSettings.Host --port ([string]$databaseSettings.Port) --username $databaseSettings.Username `
            --dbname $databaseSettings.Database --file $auditQueryFile
        $auditExitCode = $LASTEXITCODE
        if ($auditExitCode -ne 0) { throw 'No se pudo verificar la auditoría final.' }
        $auditCount = [int](($auditOutput -join '').Trim())
    } finally {
        Remove-Item -LiteralPath $auditQueryFile -Force -ErrorAction SilentlyContinue
        if ($null -eq $previousPgPassword) { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue } else { $env:PGPASSWORD = $previousPgPassword }
        if ($null -eq $previousPgClientEncoding) { Remove-Item Env:PGCLIENTENCODING -ErrorAction SilentlyContinue } else { $env:PGCLIENTENCODING = $previousPgClientEncoding }
    }

    if ($verified -ne 81 -or $verifiedTotal -ne $expectedTotal -or $auditCount -ne 81) {
        throw "Verificación final distinta a lo esperado: $verified cuentas, $auditCount eventos de auditoría."
    }
    Write-Host ''
    Write-Host ("Carga completa: {0} cuentas verificadas, total {1} USD, {2} eventos de auditoría." -f $verified, $verifiedTotal.ToString([Globalization.CultureInfo]::InvariantCulture), $auditCount) -ForegroundColor Green
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    if (($imported + $replayed) -gt 0) {
        Write-Host ("El servicio confirmó {0} saldos nuevos y {1} reintentos. La comprobación final no terminó; vuelve a ejecutar el importador actualizado para verificar y completar los que falten." -f $imported, $replayed) -ForegroundColor Yellow
    }
    exit 1
} finally {
    if ($null -ne $accessToken) {
        try { $null = Invoke-PosApiRequest -Method POST -Path 'api/v1/auth/logout' -AccessToken $accessToken } catch { }
    }
    if ($null -ne $pinSecure) { $pinSecure.Dispose() }
    $pinText = $null
    if ($null -ne $databaseSettings) { $databaseSettings.Password = $null }
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
