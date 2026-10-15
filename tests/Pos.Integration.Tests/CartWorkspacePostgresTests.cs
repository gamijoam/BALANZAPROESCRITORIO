using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sales;
using Pos.Shared.Contracts;

namespace Pos.Integration.Tests;

public sealed class CartWorkspacePostgresTests
{
    [CartWorkspacePostgresFact]
    public async Task PausingMultipleCartsPersistsThemWithoutChangingSalesStockOrCash()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("POS_CART_WORKSPACE_TEST_ADMIN_CONNECTION");
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);

        var databaseName = "pos_cart_workspaces_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var databaseCreated = false;
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", admin))
                await create.ExecuteNonQueryAsync();
            databaseCreated = true;

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
            var options = new DbContextOptionsBuilder<PosDbContext>()
                .UseNpgsql(testBuilder.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName))
                .Options;
            await using (var db = new PosDbContext(options))
            {
                await db.Database.MigrateAsync();
                var operatorId = Guid.NewGuid();
                var anotherOperatorId = Guid.NewGuid();
                var firstProductId = Guid.NewGuid();
                var secondProductId = Guid.NewGuid();
                db.OperatorUsers.Add(new OperatorUserRecord
                {
                    Id = operatorId,
                    Username = "cart-workspace-test",
                    PinSalt = [1],
                    PinHash = [1],
                    CreatedAtUtc = DateTime.UtcNow
                });
                db.OperatorUsers.Add(new OperatorUserRecord
                {
                    Id = anotherOperatorId,
                    Username = "cart-workspace-other",
                    PinSalt = [2],
                    PinHash = [2],
                    CreatedAtUtc = DateTime.UtcNow
                });
                db.Products.AddRange(
                    new ProductRecord { Id = firstProductId, Sku = "CART-1", Name = "Artículo uno", UnitPrice = 4m, Currency = "VES", TaxTreatment = "TaxFree", CreatedAtUtc = DateTime.UtcNow },
                    new ProductRecord { Id = secondProductId, Sku = "CART-2", Name = "Artículo dos", UnitPrice = 7m, Currency = "VES", TaxTreatment = "TaxFree", CreatedAtUtc = DateTime.UtcNow });
                db.Stocks.AddRange(new StockRecord { ProductId = firstProductId, Quantity = 20m }, new StockRecord { ProductId = secondProductId, Quantity = 30m });
                await db.SaveChangesAsync();

                var terminalId = Guid.NewGuid();
                var service = new CartWorkspaceService(db);
                var active = await service.GetActiveAsync(terminalId, operatorId);
                active = await service.SaveActiveAsync(terminalId, operatorId,
                    new CartWorkspaceSaveRequest(active.Id, active.Version, "save-first-0001", null,
                        [new CartWorkspaceLineInput(firstProductId, 2m, "Price1")]));
                var firstPause = new CartWorkspacePauseRequest(active.Version, "pause-first-001", "Cliente vuelve luego");
                await service.PauseActiveAsync(terminalId, operatorId, firstPause);
                await service.PauseActiveAsync(terminalId, operatorId, firstPause);
                Assert.Single(await service.GetPausedAsync(terminalId));

                active = await service.GetActiveAsync(terminalId, operatorId);
                active = await service.SaveActiveAsync(terminalId, operatorId,
                    new CartWorkspaceSaveRequest(active.Id, active.Version, "save-second-001", null,
                        [new CartWorkspaceLineInput(secondProductId, 3m, "Price1")]));
                var staleSave = await Assert.ThrowsAsync<CartWorkspaceException>(() => service.SaveActiveAsync(terminalId, operatorId,
                    new CartWorkspaceSaveRequest(active.Id, active.Version - 1, "save-stale-001", null, [])));
                Assert.Equal("workspace_version_conflict", staleSave.Code);
                await service.PauseActiveAsync(terminalId, operatorId,
                    new CartWorkspacePauseRequest(active.Version, "pause-second-001", null));

                var paused = await service.GetPausedAsync(terminalId);
                Assert.Equal(2, paused.Count);
                Assert.Contains(paused, workspace => workspace.Note == "Cliente vuelve luego");
                Assert.All(paused, workspace => Assert.Equal("Paused", workspace.Status));
                Assert.Equal(50m, await db.Stocks.Where(x => x.ProductId == firstProductId || x.ProductId == secondProductId).SumAsync(x => x.Quantity));
                Assert.Equal(0, await db.Sales.CountAsync());
                Assert.Equal(0, await db.Payments.CountAsync());
                Assert.Equal(0, await db.CashMovements.CountAsync());
                Assert.Equal(0, await db.ReceivableEntries.CountAsync());
                Assert.Equal(1, await db.CartWorkspaces.CountAsync(x => x.TerminalId == terminalId && x.Status == "Active"));

                var firstPaused = paused[0];
                var changedPricing = await Assert.ThrowsAsync<CartWorkspaceException>(() => service.ResumeAsync(terminalId, anotherOperatorId,
                    firstPaused.Id, new CartWorkspaceTransitionRequest(firstPaused.Version, "resume-stale-price-001", new string('f', 64))));
                Assert.Equal("cart_values_changed", changedPricing.Code);
                Assert.Equal("Paused", await db.CartWorkspaces.Where(x => x.Id == firstPaused.Id).Select(x => x.Status).SingleAsync());

                var resumeRequest = new CartWorkspaceTransitionRequest(firstPaused.Version, "resume-cart-0001", firstPaused.PricingFingerprint);
                var resumed = await service.ResumeAsync(terminalId, anotherOperatorId, firstPaused.Id, resumeRequest);
                var resumeReplay = await service.ResumeAsync(terminalId, anotherOperatorId, firstPaused.Id, resumeRequest);
                Assert.Equal("Active", resumed.Status);
                Assert.Equal(resumed.Version, resumeReplay.Version);

                var otherPaused = paused.Single(x => x.Id != firstPaused.Id);
                var forbiddenDiscard = await Assert.ThrowsAsync<CartWorkspaceException>(() => service.DiscardAsync(terminalId,
                    anotherOperatorId, false, otherPaused.Id,
                    new CartWorkspaceTransitionRequest(otherPaused.Version, "discard-forbidden-01")));
                Assert.Equal("discard_not_allowed", forbiddenDiscard.Code);
                await service.DiscardAsync(terminalId, anotherOperatorId, true, otherPaused.Id,
                    new CartWorkspaceTransitionRequest(otherPaused.Version, "discard-supervisor-01"));
                Assert.Equal("Discarded", await db.CartWorkspaces.Where(x => x.Id == otherPaused.Id).Select(x => x.Status).SingleAsync());
                Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.TerminalId == terminalId && x.Action == "cart.paused"));
                Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.TerminalId == terminalId && x.Action == "cart.resumed"));
                Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.TerminalId == terminalId && x.Action == "cart.discarded"));
            }
        }
        finally
        {
            if (databaseCreated)
            {
                await using var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid();", admin);
                terminate.Parameters.AddWithValue("database", databaseName);
                await terminate.ExecuteNonQueryAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", admin);
                await drop.ExecuteNonQueryAsync();
            }
            adminBuilder.Password = string.Empty;
        }
    }
}

public sealed class CartWorkspacePostgresFactAttribute : FactAttribute
{
    public CartWorkspacePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POS_CART_WORKSPACE_TEST_ADMIN_CONNECTION")))
            Skip = "Configura POS_CART_WORKSPACE_TEST_ADMIN_CONNECTION para ejecutar la prueba con una base PostgreSQL desechable.";
    }
}
