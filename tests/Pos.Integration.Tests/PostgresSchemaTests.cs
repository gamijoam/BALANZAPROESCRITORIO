using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Finance;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sales;

namespace Pos.Integration.Tests;

public sealed class PostgresSchemaTests
{
    [Fact]
    public async Task Initial_migration_creates_sale_idempotency_and_audit_tables()
    {
        var connectionString = ReadLocalConnectionString();
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new PosDbContext(options);

        Assert.True(await db.Database.CanConnectAsync());
        Assert.Contains("20261009013337_InitialPosSchema", await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var tableCount = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
              ('products','stocks','sales','sale_lines','sale_payments','idempotency_records','audit_events')
            """).SingleAsync();
        Assert.Equal(7, tableCount);
    }

    [Fact]
    public async Task Identity_migration_creates_operator_and_session_tables()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        await using var db = new PosDbContext(options);

        var tableCount = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN ('operator_users','access_sessions')
            """).SingleAsync();

        Assert.Equal(2, tableCount);
    }

    [Fact]
    public async Task Hito4_migrations_create_inventory_cash_reconciliation_customers_and_rate_history()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        await using var db = new PosDbContext(options);
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), x => x.Contains("Hito4InventoryCashSecurity", StringComparison.Ordinal));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), x => x.Contains("Hito4CustomersPricingAndRates", StringComparison.Ordinal));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), x => x.Contains("MixedCurrencySaleSnapshots", StringComparison.Ordinal));

        var tableCount = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
              ('stock_movements','cash_movements','price_history','exchange_rates','customers','receivable_entries')
            """).SingleAsync();
        var reconciliationColumnCount = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'cash_sessions'
              AND column_name IN ('ExpectedClosingBalance','CountedClosingBalance','Variance')
            """).SingleAsync();
        var fxSnapshotColumnCount = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'sale_lines'
              AND column_name IN ('OriginalUnitPrice','OriginalCurrency','ExchangeRateId','AppliedExchangeRate')
            """).SingleAsync();

        Assert.Equal(6, tableCount);
        Assert.Equal(3, reconciliationColumnCount);
        Assert.Equal(4, fxSnapshotColumnCount);
    }

    [Fact]
    public async Task Authentication_uses_hashed_tokens_and_revocable_sessions()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        await using var db = new PosDbContext(options);
        var auth = new AuthenticationService(db);
        var username = $"test-{Guid.NewGuid():N}";
        var terminalId = Guid.NewGuid();
        var operatorId = await auth.CreateOperatorAsync(username, "4826");

        Assert.Null(await auth.AuthenticateAsync(username, "0000", terminalId));
        var result = await auth.AuthenticateAsync(username.ToUpperInvariant(), "4826", terminalId);
        Assert.NotNull(result);
        Assert.Equal(operatorId, result.OperatorId);
        Assert.Equal(terminalId, result.TerminalId);
        var session = await auth.FindValidSessionAsync(result.Token);
        Assert.NotNull(session);
        Assert.NotEqual(result.Token, session.TokenHash);

        await auth.RevokeSessionAsync(result.Token);
        Assert.Null(await auth.FindValidSessionAsync(result.Token));

        await db.AccessSessions.Where(x => x.OperatorId == operatorId).ExecuteDeleteAsync();
        await db.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Checkout_is_atomic_and_idempotent_per_terminal()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var terminalId = Guid.NewGuid();
        var username = $"sale-{Guid.NewGuid():N}";
        var productId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        Guid operatorId;
        Guid firstSaleId;
        await using (var db = new PosDbContext(options))
        {
            operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "4826");
            db.Products.Add(new ProductRecord { Id = productId, Sku = $"TEST-{productId:N}", Name = "Test item", UnitPrice = 10, Currency = "VES", CreatedAtUtc = DateTime.UtcNow });
            db.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 2 });
            db.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpeningBalance = 0, OpenedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var sales = new SalesService(db);
            var request = new CheckoutRequest([new CheckoutLine(productId, 1)], [new CheckoutPayment("cash", 10, "VES")]);
            var first = await sales.ConfirmAsync(operatorId, terminalId, cashId, "integration-key-001", request);
            firstSaleId = first.SaleId;
            Assert.False(first.Replayed);
            var replay = await sales.ConfirmAsync(operatorId, terminalId, cashId, "integration-key-001", request);
            Assert.True(replay.Replayed);
            Assert.Equal(firstSaleId, replay.SaleId);
            var conflict = await sales.ConfirmAsync(operatorId, terminalId, cashId, "integration-key-001",
                new CheckoutRequest([new CheckoutLine(productId, 1)], [new CheckoutPayment("cash", 9, "VES")]));
            Assert.Equal("idempotency_conflict", conflict.Error);

            await Assert.ThrowsAsync<CheckoutRejectedException>(() => sales.ConfirmAsync(operatorId, terminalId, cashId, "integration-key-002",
                new CheckoutRequest([new CheckoutLine(productId, 2)], [new CheckoutPayment("cash", 20, "VES")])));
        }

        await using (var verify = new PosDbContext(options))
        {
            Assert.Equal(1, await verify.Stocks.AsNoTracking().Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync());
            Assert.Equal(1, await verify.Sales.CountAsync(x => x.Id == firstSaleId));
            Assert.Equal(1, await verify.IdempotencyRecords.CountAsync(x => x.TerminalId == terminalId));
            Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TerminalId == terminalId && x.Action == "sale.confirmed"));
            await verify.SaleLines.Where(x => x.SaleId == firstSaleId).ExecuteDeleteAsync();
            await verify.Payments.Where(x => x.SaleId == firstSaleId).ExecuteDeleteAsync();
            await verify.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.Sales.Where(x => x.Id == firstSaleId).ExecuteDeleteAsync();
            await verify.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
            await verify.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
            await verify.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
            await verify.AccessSessions.Where(x => x.OperatorId == operatorId).ExecuteDeleteAsync();
            await verify.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Partial_and_full_returns_are_atomic_idempotent_and_reconcile_stock_and_cash()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var terminalId = Guid.NewGuid(); var username = $"return-{Guid.NewGuid():N}";
        var productId = Guid.NewGuid(); var cashId = Guid.NewGuid();
        Guid operatorId; Guid saleId;
        await using (var db = new PosDbContext(options))
        {
            operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "4826");
            db.Products.Add(new ProductRecord { Id = productId, Sku = $"RETURN-{productId:N}", Name = "Return item", UnitPrice = 10m, Currency = "VES", CreatedAtUtc = DateTime.UtcNow });
            db.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 3m });
            db.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpenedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var sale = await new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, "return-sale-0001",
                new CheckoutRequest([new CheckoutLine(productId, 2m)], [new CheckoutPayment("cash", 20m, "VES")]));
            saleId = sale.SaleId;
            var saleLine = await db.SaleLines.SingleAsync(x => x.SaleId == saleId);
            var returns = new SaleReturnService(db);
            var partialRequest = new SaleReturnRequest("Producto devuelto en buen estado", [new SaleReturnLineRequest(saleLine.Id, 1m, true)]);

            var first = await returns.ReturnAsync(operatorId, terminalId, cashId, saleId, "return-key-0001", partialRequest);
            var replay = await returns.ReturnAsync(operatorId, terminalId, cashId, saleId, "return-key-0001", partialRequest);
            Assert.False(first.Replayed);
            Assert.True(replay.Replayed);
            Assert.Equal(first.ReturnId, replay.ReturnId);
            Assert.Equal(10m, first.Amount);

            var final = await returns.ReturnAsync(operatorId, terminalId, cashId, saleId, "return-key-0002",
                new SaleReturnRequest("Producto restante no revendible", [new SaleReturnLineRequest(saleLine.Id, 1m, false)]));
            Assert.Equal(10m, final.Amount);
            await Assert.ThrowsAsync<CheckoutRejectedException>(() => returns.ReturnAsync(operatorId, terminalId, cashId, saleId, "return-key-0003",
                new SaleReturnRequest("Devolución excedida", [new SaleReturnLineRequest(saleLine.Id, 1m, true)])));
        }

        await using (var verify = new PosDbContext(options))
        {
            Assert.Equal(2m, await verify.Stocks.Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync());
            Assert.Equal("Returned", await verify.Sales.Where(x => x.Id == saleId).Select(x => x.Status).SingleAsync());
            Assert.Equal(2, await verify.SaleReturns.CountAsync(x => x.SaleId == saleId));
            var returnIds = await verify.SaleReturns.Where(x => x.SaleId == saleId).Select(x => x.Id).ToArrayAsync();
            Assert.Equal(2, await verify.SaleReturnPayments.CountAsync(x => returnIds.Contains(x.SaleReturnId)));
            Assert.Equal(20m, await verify.CashMovements.Where(x => x.CashSessionId == cashId && x.Direction == "Out").SumAsync(x => x.Amount));
            Assert.Equal(1, await verify.StockMovements.CountAsync(x => x.ProductId == productId && x.QuantityDelta > 0m));
            Assert.Equal(2, await verify.AuditEvents.CountAsync(x => x.TerminalId == terminalId && x.Action == "sale.returned"));

            await verify.SaleReturnPayments.Where(x => returnIds.Contains(x.SaleReturnId)).ExecuteDeleteAsync();
            await verify.SaleReturnLines.Where(x => returnIds.Contains(x.SaleReturnId)).ExecuteDeleteAsync();
            await verify.ReturnIdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.SaleReturns.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
            await verify.StockMovements.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
            await verify.CashMovements.Where(x => x.CashSessionId == cashId).ExecuteDeleteAsync();
            await verify.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.SaleLines.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
            await verify.Payments.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
            await verify.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.Sales.Where(x => x.Id == saleId).ExecuteDeleteAsync();
            await verify.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
            await verify.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
            await verify.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
            await verify.AccessSessions.Where(x => x.OperatorId == operatorId).ExecuteDeleteAsync();
            await verify.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Credit_sale_checks_customer_limit_and_records_the_receivable_atomically()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var terminalId = Guid.NewGuid(); var username = $"credit-{Guid.NewGuid():N}";
        var productId = Guid.NewGuid(); var customerId = Guid.NewGuid(); var cashId = Guid.NewGuid(); var rateId = Guid.NewGuid();
        Guid operatorId; Guid saleId;
        await using (var db = new PosDbContext(options))
        {
            operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "4826");
            var usdToVes = new ExchangeRateRecord { Id = rateId, FromCurrency = "USD", ToCurrency = "VES", Rate = 875.65m, EffectiveAtUtc = DateTime.UtcNow.AddMinutes(-1), Source = "integration-test", ActorId = operatorId, CreatedAtUtc = DateTime.UtcNow };
            db.ExchangeRates.Add(usdToVes);
            db.Products.Add(new ProductRecord { Id = productId, Sku = $"CREDIT-{productId:N}", Name = "Credit item", UnitPrice = 875.65m, Currency = "VES", CreatedAtUtc = DateTime.UtcNow });
            db.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 2m });
            db.Customers.Add(new CustomerRecord { Id = customerId, Name = "Credit customer", Currency = "USD", CreditLimit = 0.99m, CreatedAtUtc = DateTime.UtcNow });
            db.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpenedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var checkout = new CheckoutRequest([new CheckoutLine(productId, 1m)], [new CheckoutPayment("credit", 875.65m, "VES")], customerId, usdToVes.Id);

            var rejected = await Assert.ThrowsAsync<CheckoutRejectedException>(() => new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, "credit-key-0001", checkout));
            Assert.Equal("credit_limit_exceeded", rejected.Code);
            Assert.Equal(2m, await db.Stocks.Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync());
            Assert.Empty(await db.Sales.Where(x => x.TerminalId == terminalId).ToListAsync());

            var customer = await db.Customers.SingleAsync(x => x.Id == customerId);
            customer.CreditLimit = 1m;
            await db.SaveChangesAsync();
            var accepted = await new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, "credit-key-0002", checkout);
            saleId = accepted.SaleId;
            Assert.False(accepted.Replayed);
            var charge = await db.ReceivableEntries.SingleAsync(x => x.CustomerId == customerId && x.EntryType == "Charge");
            Assert.Equal(1m, charge.Amount);
            Assert.Equal("USD", charge.Currency);
            Assert.Equal(875.65m, charge.OriginalAmount);
            Assert.Equal("VES", charge.OriginalCurrency);
            Assert.Equal(CreditAccountCurrency.UsdPerVes(usdToVes.Rate), charge.AppliedExchangeRate);
            var paymentUsd = CreditAccountCurrency.ToUsd(525.39m, "VES", usdToVes.Rate);
            db.ReceivableEntries.Add(new ReceivableEntryRecord { Id = Guid.NewGuid(), CustomerId = customerId, EntryType = "Payment", Amount = paymentUsd, Currency = "USD", OriginalAmount = 525.39m, OriginalCurrency = "VES", AppliedExchangeRate = CreditAccountCurrency.UsdPerVes(usdToVes.Rate), Reason = "Integration abono", ActorId = operatorId, TerminalId = terminalId, OccurredAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            Assert.Equal(0.40m, await db.ReceivableEntries.Where(x => x.CustomerId == customerId).SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount));
            Assert.Equal(1m, await db.Stocks.Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync());
        }

        await using (var cleanup = new PosDbContext(options))
        {
            await cleanup.ReceivableEntries.Where(x => x.CustomerId == customerId).ExecuteDeleteAsync();
            await cleanup.SaleLines.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
            await cleanup.ExchangeRates.Where(x => x.Id == rateId).ExecuteDeleteAsync();
            await cleanup.Payments.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
            await cleanup.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.Sales.Where(x => x.Id == saleId).ExecuteDeleteAsync();
            await cleanup.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
            await cleanup.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
            await cleanup.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
            await cleanup.Customers.Where(x => x.Id == customerId).ExecuteDeleteAsync();
            await cleanup.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Mixed_currency_checkout_converts_each_line_to_cash_currency_using_the_current_rate()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var terminalId = Guid.NewGuid(); var username = $"fx-{Guid.NewGuid():N}";
        var usdProductId = Guid.NewGuid(); var vesProductId = Guid.NewGuid(); var cashId = Guid.NewGuid(); var cashVesId = Guid.NewGuid();
        var rateId = Guid.NewGuid(); Guid operatorId; Guid saleId; Guid reverseSaleId;
        await using (var db = new PosDbContext(options))
        {
            operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "4826");
            db.Products.AddRange(
                new ProductRecord { Id = usdProductId, Sku = $"USD-{usdProductId:N}", Name = "USD item", UnitPrice = 2m, Currency = "USD", CreatedAtUtc = DateTime.UtcNow },
                new ProductRecord { Id = vesProductId, Sku = $"VES-{vesProductId:N}", Name = "VES item", UnitPrice = 80m, Currency = "VES", CreatedAtUtc = DateTime.UtcNow });
            db.Stocks.AddRange(new StockRecord { ProductId = usdProductId, Quantity = 2m }, new StockRecord { ProductId = vesProductId, Quantity = 2m });
            db.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "USD", OpenedAtUtc = DateTime.UtcNow });
            db.ExchangeRates.Add(new ExchangeRateRecord { Id = rateId, FromCurrency = "VES", ToCurrency = "USD", Rate = 0.025m, EffectiveAtUtc = DateTime.UtcNow.AddMinutes(-1), Source = "integration-test", ActorId = operatorId, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var request = new CheckoutRequest(
                [new CheckoutLine(usdProductId, 1m), new CheckoutLine(vesProductId, 1m)],
                [new CheckoutPayment("cash", 4m, "USD")],
                ExchangeRateId: rateId);
            var staleRate = await Assert.ThrowsAsync<CheckoutRejectedException>(() => new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, "fx-checkout-stale", request with { ExchangeRateId = Guid.NewGuid() }));
            Assert.Equal("exchange_rate_changed", staleRate.Code);
            var result = await new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, "fx-checkout-0001", request);
            saleId = result.SaleId;

            Assert.False(result.Replayed);
            var sale = await db.Sales.SingleAsync(x => x.Id == saleId);
            Assert.Equal("USD", sale.Currency);
            Assert.Equal(4m, sale.Total);
            Assert.Equal(new[] { 2m, 2m }, await db.SaleLines.Where(x => x.SaleId == saleId).OrderBy(x => x.Sku).Select(x => x.UnitPrice).ToArrayAsync());
            var convertedLine = await db.SaleLines.SingleAsync(x => x.SaleId == saleId && x.ProductId == vesProductId);
            Assert.Equal(80m, convertedLine.OriginalUnitPrice);
            Assert.Equal("VES", convertedLine.OriginalCurrency);
            Assert.Equal(rateId, convertedLine.ExchangeRateId);
            Assert.Equal(0.025m, convertedLine.AppliedExchangeRate);
            Assert.Contains(rateId.ToString(), await db.AuditEvents.Where(x => x.TerminalId == terminalId && x.Action == "sale.confirmed").Select(x => x.Payload).SingleAsync());

            var usdCashSession = await db.CashSessions.SingleAsync(x => x.Id == cashId);
            usdCashSession.Status = "Closed";
            usdCashSession.ClosedAtUtc = DateTime.UtcNow;
            db.CashSessions.Add(new CashSessionRecord { Id = cashVesId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpenedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var reverse = await new SalesService(db).ConfirmAsync(operatorId, terminalId, cashVesId, "fx-checkout-0002",
                new CheckoutRequest([new CheckoutLine(usdProductId, 1m)], [new CheckoutPayment("cash", 80m, "VES")], ExchangeRateId: rateId));
            reverseSaleId = reverse.SaleId;
            Assert.Equal(80m, await db.SaleLines.Where(x => x.SaleId == reverseSaleId).Select(x => x.UnitPrice).SingleAsync());
            Assert.Equal("VES", await db.Sales.Where(x => x.Id == reverseSaleId).Select(x => x.Currency).SingleAsync());
        }

        await using (var cleanup = new PosDbContext(options))
        {
            var saleIds = new[] { saleId, reverseSaleId };
            await cleanup.SaleLines.Where(x => saleIds.Contains(x.SaleId)).ExecuteDeleteAsync();
            await cleanup.Payments.Where(x => saleIds.Contains(x.SaleId)).ExecuteDeleteAsync();
            await cleanup.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.Sales.Where(x => saleIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.ExchangeRates.Where(x => x.Id == rateId).ExecuteDeleteAsync();
            await cleanup.CashSessions.Where(x => x.Id == cashId || x.Id == cashVesId).ExecuteDeleteAsync();
            await cleanup.Stocks.Where(x => x.ProductId == usdProductId || x.ProductId == vesProductId).ExecuteDeleteAsync();
            await cleanup.Products.Where(x => x.Id == usdProductId || x.Id == vesProductId).ExecuteDeleteAsync();
            await cleanup.AccessSessions.Where(x => x.OperatorId == operatorId).ExecuteDeleteAsync();
            await cleanup.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Checkout_accepts_and_audits_usd_cent_rounding_while_cash_close_counts_actual_usd()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var now = DateTime.UtcNow; var terminalId = Guid.NewGuid(); var productId = Guid.NewGuid();
        var cashId = Guid.NewGuid(); var rateId = Guid.NewGuid(); var username = $"rounding-{Guid.NewGuid():N}";
        Guid operatorId = Guid.Empty; Guid saleId = Guid.Empty;
        try
        {
            await using (var db = new PosDbContext(options))
            {
                operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "4826");
                db.Products.Add(new ProductRecord { Id = productId, Sku = $"ROUND-{productId:N}", Name = "Producto de redondeo", UnitPrice = 1_000m, Currency = "VES", CreatedAtUtc = now });
                db.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 1m });
                db.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpeningBalanceVes = 0m, OpeningBalanceUsd = 0m, OpenedAtUtc = now });
                db.ExchangeRates.Add(new ExchangeRateRecord { Id = rateId, FromCurrency = "USD", ToCurrency = "VES", Rate = 36.50m, EffectiveAtUtc = now.AddSeconds(-1), Source = "integration-test", ActorId = operatorId, CreatedAtUtc = now });
                await db.SaveChangesAsync();

                var sales = new SalesService(db);
                var underpaid = new CheckoutRequest([new CheckoutLine(productId, 1m)], [new CheckoutPayment("Efectivo", 27.39m, "USD")], ExchangeRateId: rateId);
                var rejected = await Assert.ThrowsAsync<CheckoutRejectedException>(() => sales.ConfirmAsync(operatorId, terminalId, cashId, "rounding-under-0001", underpaid));
                Assert.Equal("payment_total_mismatch", rejected.Code);

                var accepted = await sales.ConfirmAsync(operatorId, terminalId, cashId, "rounding-accepted-01",
                    underpaid with { Payments = [new CheckoutPayment("Efectivo", 27.40m, "USD")] });
                saleId = accepted.SaleId;
                var sale = await db.Sales.SingleAsync(x => x.Id == saleId);
                var payment = await db.Payments.SingleAsync(x => x.SaleId == saleId);
                Assert.Equal(1_000m, sale.Total);
                Assert.Equal("VES", sale.Currency);
                Assert.Equal(-0.10m, sale.RoundingAdjustment);
                Assert.Equal(27.40m, payment.Amount);
                Assert.Equal("USD", payment.Currency);
                var audit = await db.AuditEvents.SingleAsync(x => x.TerminalId == terminalId && x.Action == "sale.confirmed");
                using var auditPayload = System.Text.Json.JsonDocument.Parse(audit.Payload);
                Assert.Equal(-0.10m, auditPayload.RootElement.GetProperty("roundingAdjustment").GetDecimal());
                Assert.Equal(0.18250m, auditPayload.RootElement.GetProperty("allowedRoundingDifference").GetDecimal());

                var close = CashSessionLedger.Calculate(0m, 0m, [new CashReceipt(payment.Currency, payment.Amount)], [], 27.40m, 0m);
                Assert.Equal(27.40m, close.Usd.ExpectedBalance);
                Assert.Equal(0m, close.Ves.ExpectedBalance);
                Assert.Equal(0m, close.Usd.Variance);
            }
        }
        finally
        {
            await using var cleanup = new PosDbContext(options);
            if (saleId != Guid.Empty)
            {
                await cleanup.Payments.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
                await cleanup.SaleLines.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
                await cleanup.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
                await cleanup.Sales.Where(x => x.Id == saleId).ExecuteDeleteAsync();
            }
            if (operatorId != Guid.Empty)
            {
                await cleanup.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
                await cleanup.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
                await cleanup.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
                await cleanup.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
                await cleanup.ExchangeRates.Where(x => x.Id == rateId).ExecuteDeleteAsync();
                await cleanup.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
            }
        }
    }

    [Fact]
    public async Task Zero_opening_cash_session_records_mixed_currency_sale_for_sales_and_cash_reports()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var now = DateTime.UtcNow; var terminalId = Guid.NewGuid(); var productId = Guid.NewGuid();
        var cashId = Guid.NewGuid(); var rateId = Guid.NewGuid(); var username = $"cash-report-{Guid.NewGuid():N}";
        Guid operatorId = Guid.Empty; Guid saleId = Guid.Empty;
        try
        {
            await using (var db = new PosDbContext(options))
            {
                operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "4826");
                db.Products.Add(new ProductRecord { Id = productId, Sku = $"CASH-{productId:N}", Name = "Cash report item", UnitPrice = 100m, Currency = "VES", CreatedAtUtc = now });
                db.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 1m });
                db.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpeningBalance = 0m, OpeningBalanceUsd = 0m, OpeningBalanceVes = 0m, OpenedAtUtc = now });
                db.ExchangeRates.Add(new ExchangeRateRecord { Id = rateId, FromCurrency = "USD", ToCurrency = "VES", Rate = 100m, EffectiveAtUtc = now.AddMinutes(-1), Source = "integration-test", ActorId = operatorId, CreatedAtUtc = now });
                await db.SaveChangesAsync();

                var checkout = new CheckoutRequest([new CheckoutLine(productId, 1m)],
                    [new CheckoutPayment("Efectivo", 0.5m, "USD"), new CheckoutPayment("Efectivo", 50m, "VES")], ExchangeRateId: rateId);
                var confirmed = await new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, "cash-report-flow-001", checkout);
                saleId = confirmed.SaleId;

                var salesReportRows = await db.Sales.AsNoTracking().Where(x => x.CreatedAtUtc >= now.AddMinutes(-1) && x.CreatedAtUtc < DateTime.UtcNow.AddMinutes(1) && x.Status == "Confirmed")
                    .Select(x => new { x.Id, x.Total, x.Currency }).ToListAsync();
                Assert.Contains(salesReportRows, x => x.Id == saleId && x.Total == 100m && x.Currency == "VES");
                Assert.Single(await db.SaleLines.AsNoTracking().Where(x => x.SaleId == saleId).ToListAsync());

                var payments = await db.Payments.AsNoTracking().Where(x => x.SaleId == saleId && (x.Method.ToLower() == "cash" || x.Method.ToLower() == "efectivo"))
                    .Select(x => new CashReceipt(x.Currency, x.Amount)).ToListAsync();
                var close = CashSessionLedger.Calculate(0m, 0m, payments, [], 0.5m, 50m);
                Assert.Equal(0.5m, close.Usd.ExpectedBalance);
                Assert.Equal(50m, close.Ves.ExpectedBalance);
            }
        }
        finally
        {
            if (operatorId != Guid.Empty)
            {
                await using var cleanup = new PosDbContext(options);
                if (saleId != Guid.Empty)
                {
                    await cleanup.SaleLines.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
                    await cleanup.Payments.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
                    await cleanup.IdempotencyRecords.Where(x => x.SaleId == saleId).ExecuteDeleteAsync();
                    await cleanup.AuditEvents.Where(x => x.ActorId == operatorId).ExecuteDeleteAsync();
                    await cleanup.Sales.Where(x => x.Id == saleId).ExecuteDeleteAsync();
                }
                await cleanup.ExchangeRates.Where(x => x.Id == rateId).ExecuteDeleteAsync();
                await cleanup.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
                await cleanup.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
                await cleanup.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
                await cleanup.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
            }
        }
    }

    [Fact]
    public async Task Concurrent_checkouts_cannot_sell_the_last_unit_twice()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var terminalId = Guid.NewGuid();
        var username = $"race-{Guid.NewGuid():N}";
        var productId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        Guid operatorId;
        await using (var setup = new PosDbContext(options))
        {
            operatorId = await new AuthenticationService(setup).CreateOperatorAsync(username, "4826");
            setup.Products.Add(new ProductRecord { Id = productId, Sku = $"RACE-{productId:N}", Name = "Last unit", UnitPrice = 7, Currency = "VES", CreatedAtUtc = DateTime.UtcNow });
            setup.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 1 });
            setup.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = operatorId, ActiveOperatorId = operatorId, TerminalId = terminalId, Currency = "VES", OpenedAtUtc = DateTime.UtcNow });
            await setup.SaveChangesAsync();
        }

        async Task<bool> AttemptAsync(string key)
        {
            await using var db = new PosDbContext(options);
            try
            {
                var checkout = new CheckoutRequest([new CheckoutLine(productId, 1)], [new CheckoutPayment("cash", 7, "VES")]);
                await new SalesService(db).ConfirmAsync(operatorId, terminalId, cashId, key, checkout);
                return true;
            }
            catch (CheckoutRejectedException exception) when (exception.Code == "insufficient_stock") { return false; }
        }

        var outcomes = await Task.WhenAll(AttemptAsync("race-key-0001"), AttemptAsync("race-key-0002"));
        Assert.Single(outcomes, x => x);
        await using (var verify = new PosDbContext(options))
        {
            Assert.Equal(0, await verify.Stocks.AsNoTracking().Where(x => x.ProductId == productId).Select(x => x.Quantity).SingleAsync());
            Assert.Equal(1, await verify.Sales.CountAsync(x => x.TerminalId == terminalId));
            Assert.Equal(1, await verify.IdempotencyRecords.CountAsync(x => x.TerminalId == terminalId));
            var saleIds = await verify.Sales.Where(x => x.TerminalId == terminalId).Select(x => x.Id).ToArrayAsync();
            await verify.SaleLines.Where(x => saleIds.Contains(x.SaleId)).ExecuteDeleteAsync();
            await verify.Payments.Where(x => saleIds.Contains(x.SaleId)).ExecuteDeleteAsync();
            await verify.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.Sales.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await verify.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
            await verify.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
            await verify.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
            await verify.AccessSessions.Where(x => x.OperatorId == operatorId).ExecuteDeleteAsync();
            await verify.OperatorUsers.Where(x => x.Id == operatorId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Different_authenticated_operator_can_sell_through_the_open_terminal_cash_session()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var terminalId = Guid.NewGuid();
        var openerId = Guid.Empty;
        var sellerId = Guid.Empty;
        var cashId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        await using (var setup = new PosDbContext(options))
        {
            var auth = new AuthenticationService(setup);
            openerId = await auth.CreateOperatorAsync($"cash-owner-{Guid.NewGuid():N}", "4826");
            sellerId = await auth.CreateOperatorAsync($"cash-seller-{Guid.NewGuid():N}", "4826");
            setup.Products.Add(new ProductRecord { Id = productId, Sku = $"SWITCH-{productId:N}", Name = "Cambio de operador", UnitPrice = 10m, Currency = "VES", CreatedAtUtc = DateTime.UtcNow });
            setup.Stocks.Add(new StockRecord { ProductId = productId, Quantity = 2m });
            setup.CashSessions.Add(new CashSessionRecord { Id = cashId, OperatorId = openerId, ActiveOperatorId = openerId, TerminalId = terminalId, Currency = "VES", OpenedAtUtc = DateTime.UtcNow });
            await setup.SaveChangesAsync();
        }

        try
        {
            await using (var deniedDb = new PosDbContext(options))
            {
                var checkoutBeforeClaim = new CheckoutRequest([new CheckoutLine(productId, 1m)], [new CheckoutPayment("cash", 10m, "VES")]);
                var denied = await Assert.ThrowsAsync<CheckoutRejectedException>(() => new SalesService(deniedDb)
                    .ConfirmAsync(sellerId, terminalId, cashId, $"switch-denied-{Guid.NewGuid():N}", checkoutBeforeClaim));
                Assert.Equal("open_cash_session_required", denied.Code);
            }

            await using (var claimDb = new PosDbContext(options))
            {
                var session = await claimDb.CashSessions.SingleAsync(x => x.Id == cashId);
                session.ActiveOperatorId = sellerId;
                claimDb.AuditEvents.Add(new AuditRecord { Id = Guid.NewGuid(), ActorId = sellerId, TerminalId = terminalId,
                    Action = "cash-session.operator_claimed", Payload = "{}", OccurredAtUtc = DateTime.UtcNow });
                await claimDb.SaveChangesAsync();
            }

            await using var db = new PosDbContext(options);
            var checkoutAfterClaim = new CheckoutRequest([new CheckoutLine(productId, 1m)], [new CheckoutPayment("cash", 10m, "VES")]);
            var result = await new SalesService(db).ConfirmAsync(sellerId, terminalId, cashId, $"switch-{Guid.NewGuid():N}", checkoutAfterClaim);

            Assert.False(result.Replayed);
            var sale = await db.Sales.AsNoTracking().SingleAsync(x => x.Id == result.SaleId);
            Assert.Equal(sellerId, sale.OperatorId);
            Assert.Equal(cashId, sale.CashSessionId);
        }
        finally
        {
            await using var cleanup = new PosDbContext(options);
            var saleIds = await cleanup.Sales.Where(x => x.CashSessionId == cashId).Select(x => x.Id).ToArrayAsync();
            await cleanup.SaleLines.Where(x => saleIds.Contains(x.SaleId)).ExecuteDeleteAsync();
            await cleanup.Payments.Where(x => saleIds.Contains(x.SaleId)).ExecuteDeleteAsync();
            await cleanup.IdempotencyRecords.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.Sales.Where(x => saleIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.Stocks.Where(x => x.ProductId == productId).ExecuteDeleteAsync();
            await cleanup.Products.Where(x => x.Id == productId).ExecuteDeleteAsync();
            await cleanup.CashSessions.Where(x => x.Id == cashId).ExecuteDeleteAsync();
            await cleanup.AccessSessions.Where(x => x.OperatorId == openerId || x.OperatorId == sellerId).ExecuteDeleteAsync();
            await cleanup.OperatorUsers.Where(x => x.Id == openerId || x.Id == sellerId).ExecuteDeleteAsync();
        }
    }

    private static string ReadLocalConnectionString()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("POS_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", "f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5", "secrets.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("ConnectionStrings").GetProperty("Pos").GetString()
            ?? throw new InvalidOperationException("Local PostgreSQL connection string is missing.");
    }

}
