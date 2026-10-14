using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Receivables;

namespace Pos.Integration.Tests;

public sealed class ReceivableOpeningBalanceTests
{
    [Fact]
    public async Task Opening_balance_is_exact_audited_and_idempotent()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        var customerId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var idempotencyKey = $"legacy-opening-{Guid.NewGuid():N}";
        var username = $"opening-{Guid.NewGuid():N}";
        Guid operatorId;

        try
        {
            await using (var db = new PosDbContext(options))
            {
                operatorId = await new AuthenticationService(db).CreateOperatorAsync(username, "731942");
                db.Customers.Add(new CustomerRecord
                {
                    Id = customerId,
                    Name = "Opening balance integration test",
                    Currency = "USD",
                    CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            ReceivableOpeningBalanceResult first;
            await using (var db = new PosDbContext(options))
            {
                first = await new ReceivableOpeningBalanceService(db).RecordAsync(
                    customerId, 1260.0513m, "Migración de saldo legado", idempotencyKey, operatorId, terminalId);
                Assert.False(first.Replayed);
                var entry = await db.ReceivableEntries.SingleAsync(x => x.Id == first.EntryId);
                Assert.Equal("Charge", entry.EntryType);
                Assert.True(entry.IsOpeningBalance);
                Assert.Null(entry.SaleId);
                Assert.Equal(1260.0513m, entry.Amount);
                Assert.Equal(idempotencyKey, entry.IdempotencyKey);
                Assert.Equal(operatorId, entry.ActorId);
                Assert.Equal(terminalId, entry.TerminalId);
                Assert.Contains(await db.AuditEvents.Where(x => x.TerminalId == terminalId).Select(x => x.Action).ToListAsync(),
                    action => action == "receivable.opening_balance.recorded");
            }

            await using (var db = new PosDbContext(options))
            {
                var replay = await new ReceivableOpeningBalanceService(db).RecordAsync(
                    customerId, 1260.0513m, "Migración de saldo legado", idempotencyKey, operatorId, terminalId);
                Assert.True(replay.Replayed);
                Assert.Equal(first.EntryId, replay.EntryId);
                Assert.Equal(1, await db.ReceivableEntries.CountAsync(x => x.CustomerId == customerId));

                var duplicate = await Assert.ThrowsAsync<ReceivableOpeningBalanceException>(() =>
                    new ReceivableOpeningBalanceService(db).RecordAsync(
                        customerId, 2m, "Otra apertura", $"{idempotencyKey}-different", operatorId, terminalId));
                Assert.Equal("opening_balance_already_exists", duplicate.Code);
            }
        }
        finally
        {
            await using var cleanup = new PosDbContext(options);
            await cleanup.ReceivableEntries.Where(x => x.CustomerId == customerId).ExecuteDeleteAsync();
            await cleanup.AuditEvents.Where(x => x.TerminalId == terminalId).ExecuteDeleteAsync();
            await cleanup.Customers.Where(x => x.Id == customerId).ExecuteDeleteAsync();
            var operatorRecord = await cleanup.OperatorUsers.SingleOrDefaultAsync(x => x.Username == username);
            if (operatorRecord is not null)
            {
                await cleanup.AccessSessions.Where(x => x.OperatorId == operatorRecord.Id).ExecuteDeleteAsync();
                await cleanup.OperatorUsers.Where(x => x.Id == operatorRecord.Id).ExecuteDeleteAsync();
            }
        }
    }

    private static string ReadLocalConnectionString()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("POS_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", "f7b2b1b3-8d25-4d18-98ff-c301ae6b94a5", "secrets.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("ConnectionStrings").GetProperty("Pos").GetString()
            ?? throw new InvalidOperationException("Local PostgreSQL connection string is missing.");
    }
}
