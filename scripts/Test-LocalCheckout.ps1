[CmdletBinding()]
param([string]$BaseUrl = 'http://127.0.0.1:5180')

$ErrorActionPreference = 'Stop'
$username = "checkout-$([guid]::NewGuid().ToString('N'))"
$pin = '4826'
$terminalId = [guid]::NewGuid()
$productId = [guid]::NewGuid()
$creditProductId = [guid]::Empty
$customerId = [guid]::Empty
$operatorId = [guid]::Empty
$cashierId = [guid]::Empty
$cashId = [guid]::Empty
$env:PGPASSWORD = $null

function Get-HttpStatusCode([string]$Uri, [hashtable]$Headers, [string]$Body) {
    try {
        $response = Invoke-WebRequest -Method Post -Uri $Uri -Headers $Headers -ContentType 'application/json' -Body $Body
        return [int]$response.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        throw
    }
}

try {
    $secretPath = Join-Path $env:APPDATA 'Microsoft\UserSecrets\f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5\secrets.json'
    $secrets = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
    $env:PGPASSWORD = [regex]::Match($secrets.ConnectionStrings.Pos, '(?:^|;)Password=([^;]+)').Groups[1].Value
    if (-not $env:PGPASSWORD) { throw 'The local database secret is missing.' }

    $seedSql = @"
INSERT INTO products ("Id","Sku","Name","UnitPrice","Currency","Active","CreatedAtUtc")
VALUES ('$productId','SMOKE-$productId','Temporary API checkout item',10,'VES',true,now());
INSERT INTO stocks ("ProductId","Quantity","Version") VALUES ('$productId',1,0);
"@
    $seedSql | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U pos_app -d pos_dev -f -
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the temporary API product.' }

    $created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/setup/first-operator" -ContentType 'application/json' -Body (@{ username = $username; pin = $pin } | ConvertTo-Json -Compress)
    $operatorId = [guid]$created.operatorId
    $login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/login" -ContentType 'application/json' -Body (@{ username = $username; pin = $pin; terminalId = $terminalId } | ConvertTo-Json -Compress)
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }
    $session = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/auth/session" -Headers $headers
    if ($session.role -ne 'Administrator') { throw 'Initial operator must have the Administrator role.' }
    $cashierName = "cashier-$([guid]::NewGuid().ToString('N'))"
    $cashier = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/operators" -Headers $headers -ContentType 'application/json' -Body (@{ username = $cashierName; pin = '5937'; role = 'Cashier' } | ConvertTo-Json -Compress)
    $cashierId = [guid]$cashier.operatorId
    $cashierLogin = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/login" -ContentType 'application/json' -Body (@{ username = $cashierName; pin = '5937'; terminalId = [guid]::NewGuid() } | ConvertTo-Json -Compress)
    $cashierHeaders = @{ Authorization = "Bearer $($cashierLogin.accessToken)" }
    $cashierSession = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/auth/session" -Headers $cashierHeaders
    $inventoryDenied = 0
    try { Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/inventory" -Headers $cashierHeaders | Out-Null }
    catch { if ($_.Exception.Response) { $inventoryDenied = [int]$_.Exception.Response.StatusCode } else { throw } }
    if ($inventoryDenied -ne 403) { throw "Cashier role must not access inventory adjustments (session role=$($cashierSession.role), HTTP=$inventoryDenied)." }
    Invoke-RestMethod -Method Patch -Uri "$BaseUrl/api/v1/operators/$cashierId/role" -Headers $headers -ContentType 'application/json' -Body (@{ role = 'Supervisor' } | ConvertTo-Json -Compress) | Out-Null
    $promotedRole = (Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/auth/session" -Headers $cashierHeaders).role
    Invoke-RestMethod -Method Patch -Uri "$BaseUrl/api/v1/operators/$cashierId/role" -Headers $headers -ContentType 'application/json' -Body (@{ role = 'Cashier' } | ConvertTo-Json -Compress) | Out-Null
    $lastAdminStatus = 0
    try { Invoke-RestMethod -Method Patch -Uri "$BaseUrl/api/v1/operators/$operatorId/active" -Headers $headers -ContentType 'application/json' -Body (@{ active = $false } | ConvertTo-Json -Compress) | Out-Null }
    catch { if ($_.Exception.Response) { $lastAdminStatus = [int]$_.Exception.Response.StatusCode } else { throw } }
    $cash = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/cash-sessions" -Headers $headers -ContentType 'application/json' -Body (@{ openingBalance = 0; currency = 'VES' } | ConvertTo-Json -Compress)
    $cashId = [guid]$cash.id
    $currentCash = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/cash-sessions/current" -Headers $headers
    $foundProducts = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/products?query=SMOKE-$productId" -Headers $headers

    $saleBody = @{ cashSessionId = $cashId; checkout = @{ lines = @(@{ productId = $productId; quantity = 1 }); payments = @(@{ method = 'cash'; amount = 10; currency = 'VES' }) } } | ConvertTo-Json -Depth 8 -Compress
    $key = 'local-smoke-key-001'
    $first = Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/v1/sales" -Headers ($headers + @{ 'Idempotency-Key' = $key }) -ContentType 'application/json' -Body $saleBody
    $replay = Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/v1/sales" -Headers ($headers + @{ 'Idempotency-Key' = $key }) -ContentType 'application/json' -Body $saleBody
    $changedBody = @{ cashSessionId = $cashId; checkout = @{ lines = @(@{ productId = $productId; quantity = 1 }); payments = @(@{ method = 'cash'; amount = 9; currency = 'VES' }) } } | ConvertTo-Json -Depth 8 -Compress
    $conflictCode = Get-HttpStatusCode "$BaseUrl/api/v1/sales" ($headers + @{ 'Idempotency-Key' = $key }) $changedBody
    $stockSql = "SELECT to_jsonb(s)->>'Quantity' FROM stocks s WHERE to_jsonb(s)->>'ProductId'='$productId';"
    $stock = (& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -q -A -t -h 127.0.0.1 -U pos_app -d pos_dev -c $stockSql).Trim()

    $adjustment = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/inventory/$productId/adjustments" -Headers $headers -ContentType 'application/json' -Body (@{ quantityDelta = 1; reason = 'Conteo de prueba' } | ConvertTo-Json -Compress)
    $movements = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/inventory/$productId/movements" -Headers $headers
    $adjustedStockSql = "SELECT to_jsonb(s)->>'Quantity' FROM stocks s WHERE to_jsonb(s)->>'ProductId'='$productId';"
    $adjustedStock = (& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -q -A -t -h 127.0.0.1 -U pos_app -d pos_dev -c $adjustedStockSql).Trim()

    $newProduct = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/products" -Headers $headers -ContentType 'application/json' -Body (@{ sku = "CREDIT-$productId"; name = 'Temporary credit item'; unitPrice = 5; currency = 'VES'; initialStock = 5 } | ConvertTo-Json -Compress)
    $creditProductId = [guid]$newProduct.id
    $priceChange = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/products/$creditProductId/price-changes" -Headers $headers -ContentType 'application/json' -Body (@{ newPrice = 6; reason = 'Actualización de prueba' } | ConvertTo-Json -Compress)
    $priceHistory = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/products/$creditProductId/price-history" -Headers $headers
    $customer = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/customers" -Headers $headers -ContentType 'application/json' -Body (@{ name = 'Temporary credit customer'; documentNumber = "DOC-$productId"; phone = '0000000000'; currency = 'VES'; creditLimit = 10 } | ConvertTo-Json -Compress)
    $customerId = [guid]$customer.id
    $creditSale = @{ cashSessionId = $cashId; checkout = @{ customerId = $customerId; lines = @(@{ productId = $creditProductId; quantity = 1 }); payments = @(@{ method = 'credit'; amount = 6; currency = 'VES' }) } } | ConvertTo-Json -Depth 8 -Compress
    Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/v1/sales" -Headers ($headers + @{ 'Idempotency-Key' = 'local-credit-sale-001' }) -ContentType 'application/json' -Body $creditSale | Out-Null
    $balanceBeforePayment = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/customers/$customerId/receivables" -Headers $headers
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/customers/$customerId/payments" -Headers $headers -ContentType 'application/json' -Body (@{ amount = 6; currency = 'VES'; reason = 'Abono de prueba' } | ConvertTo-Json -Compress) | Out-Null
    $balanceAfterPayment = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/customers/$customerId/receivables" -Headers $headers
    $rateBody = @{ fromCurrency = 'USD'; toCurrency = 'VES'; rate = 39.5; effectiveAtUtc = [DateTime]::UtcNow.AddMinutes(-1).ToString('o'); source = 'Smoke test' } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/exchange-rates" -Headers $headers -ContentType 'application/json' -Body $rateBody | Out-Null
    $rates = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/exchange-rates?from=USD&to=VES" -Headers $headers
    $reportStart = [DateTime]::UtcNow.AddHours(-1).ToString('o')
    $reportEnd = [DateTime]::UtcNow.AddHours(1).ToString('o')
    $salesReport = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/reports/sales?fromUtc=$([uri]::EscapeDataString($reportStart))&toUtc=$([uri]::EscapeDataString($reportEnd))" -Headers $headers
    $audit = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/audit?limit=100" -Headers $headers
    $cashOverdrawStatus = 0
    try { Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/cash-sessions/$cashId/movements" -Headers $headers -ContentType 'application/json' -Body (@{ amount = 11; direction = 'Out'; reason = 'Debe rechazarse' } | ConvertTo-Json -Compress) | Out-Null }
    catch { if ($_.Exception.Response) { $cashOverdrawStatus = [int]$_.Exception.Response.StatusCode } else { throw } }
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/cash-sessions/$cashId/movements" -Headers $headers -ContentType 'application/json' -Body (@{ amount = 5; direction = 'In'; reason = 'Fondo de prueba' } | ConvertTo-Json -Compress) | Out-Null
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/cash-sessions/$cashId/movements" -Headers $headers -ContentType 'application/json' -Body (@{ amount = 2; direction = 'Out'; reason = 'Gasto de prueba' } | ConvertTo-Json -Compress) | Out-Null
    $closed = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/cash-sessions/$cashId/close" -Headers $headers -ContentType 'application/json' -Body (@{ countedClosingBalance = 12 } | ConvertTo-Json -Compress)
    Invoke-RestMethod -Method Patch -Uri "$BaseUrl/api/v1/operators/$cashierId/active" -Headers $headers -ContentType 'application/json' -Body (@{ active = $false } | ConvertTo-Json -Compress) | Out-Null
    $revokedSessionStatus = 0
    try { Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/v1/auth/session" -Headers $cashierHeaders | Out-Null }
    catch { if ($_.Exception.Response) { $revokedSessionStatus = [int]$_.Exception.Response.StatusCode } else { throw } }

    if ([guid]$currentCash.id -ne $cashId -or @($foundProducts).Count -ne 1 -or [guid]$foundProducts[0].id -ne $productId -or
        $first.StatusCode -ne 201 -or $replay.StatusCode -ne 200 -or $conflictCode -ne 409 -or [decimal]$stock -ne 0 -or
        [decimal]$adjustedStock -ne 1 -or [decimal]$adjustment.quantity -ne 1 -or @($movements).Count -ne 1 -or
        [decimal]$closed.expectedBalance -ne 13 -or [decimal]$closed.countedBalance -ne 12 -or [decimal]$closed.variance -ne -1 -or $inventoryDenied -ne 403 -or
        [decimal]$priceChange.newPrice -ne 6 -or @($priceHistory).Count -ne 1 -or [decimal]$balanceBeforePayment.balance -ne 6 -or
        [decimal]$balanceAfterPayment.balance -ne 0 -or @($rates).Count -lt 1 -or @($salesReport.sales).Count -lt 1 -or @($audit).Count -lt 1 -or
        $promotedRole -ne 'Supervisor' -or $lastAdminStatus -ne 409 -or $cashOverdrawStatus -ne 409 -or $revokedSessionStatus -ne 401) {
        throw ("Unexpected HTTP/storage result: currentCash={0}, productMatches={1}, created={2}, replay={3}, conflict={4}, stock={5}." -f $currentCash.id, @($foundProducts).Count, $first.StatusCode, $replay.StatusCode, $conflictCode, $stock)
    }
    Write-Output 'Hito 4 API smoke passed: RBAC, inventory ledger, cash reconciliation, catalog/prices, FX rates, customer credit/payment, sales report, audit and idempotent sales.'
}
finally {
    if ($operatorId -ne [guid]::Empty) {
        $cleanupSql = @"
DELETE FROM sale_lines WHERE "SaleId" IN (SELECT "Id" FROM sales WHERE "TerminalId"='$terminalId');
DELETE FROM sale_payments WHERE "SaleId" IN (SELECT "Id" FROM sales WHERE "TerminalId"='$terminalId');
DELETE FROM receivable_entries WHERE "CustomerId"='$customerId';
DELETE FROM idempotency_records WHERE "TerminalId"='$terminalId';
DELETE FROM audit_events WHERE "TerminalId"='$terminalId';
DELETE FROM sales WHERE "TerminalId"='$terminalId';
DELETE FROM price_history WHERE "ProductId"='$creditProductId';
DELETE FROM stock_movements WHERE "ProductId"='$productId';
DELETE FROM stock_movements WHERE "ProductId"='$creditProductId';
DELETE FROM stocks WHERE "ProductId"='$productId';
DELETE FROM stocks WHERE "ProductId"='$creditProductId';
DELETE FROM products WHERE "Id"='$productId';
DELETE FROM products WHERE "Id"='$creditProductId';
DELETE FROM customers WHERE "Id"='$customerId';
DELETE FROM cash_movements WHERE "CashSessionId"='$cashId';
DELETE FROM cash_sessions WHERE "Id"='$cashId';
        DELETE FROM access_sessions WHERE "OperatorId"='$operatorId';
        DELETE FROM operator_users WHERE "Id"='$operatorId';
"@
        $cleanupSql = $cleanupSql.Replace('$customerId', $customerId).Replace('$creditProductId', $creditProductId)
        $cleanupSql | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U pos_app -d pos_dev -f -
        if ($LASTEXITCODE -ne 0) { throw 'Could not clean up the temporary API checkout data.' }
    } else {
        $cleanupProductSql = "DELETE FROM stocks WHERE to_jsonb(stocks)->>'ProductId'='$productId'; DELETE FROM products WHERE to_jsonb(products)->>'Id'='$productId';"
        $cleanupProductSql | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -q -h 127.0.0.1 -U pos_app -d pos_dev -f - | Out-Null
    }
    if ($cashierId -ne [guid]::Empty) {
        $cleanupCashierSql = @"
DELETE FROM access_sessions WHERE "OperatorId"='$cashierId';
DELETE FROM operator_users WHERE "Id"='$cashierId';
"@
        $cleanupCashierSql | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U pos_app -d pos_dev -f -
        if ($LASTEXITCODE -ne 0) { throw 'Could not clean up the temporary cashier.' }
    }
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
