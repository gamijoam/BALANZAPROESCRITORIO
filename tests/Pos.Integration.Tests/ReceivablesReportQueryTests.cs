using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Reporting;

namespace Pos.Integration.Tests;

[Collection(SharedPostgresCollection.Name)]
public sealed class ReceivablesReportQueryTests
{
    [Fact]
    public async Task Reports_include_all_positive_balances_convert_with_current_rate_and_filter_utc_movements()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ReadLocalConnectionString()).Options;
        await using var db = new PosDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rateId = Guid.NewGuid();
        var customerIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();
        var fromUtc = now.AddHours(-4);
        var toUtc = now.AddHours(-1);

        try
        {
            db.ExchangeRates.Add(new ExchangeRateRecord
            {
                Id = rateId, FromCurrency = "USD", ToCurrency = "VES", Rate = 900m,
                EffectiveAtUtc = now.AddMinutes(-30), Source = "receivables-report-test",
                ActorId = Guid.NewGuid(), CreatedAtUtc = now
            });
            db.Customers.AddRange(customerIds.Select((id, index) => new CustomerRecord
            {
                Id = id, Name = $"RPT-{runId:N}-{index:D3}", Currency = "USD", CreditLimit = 500m,
                CreatedAtUtc = now
            }));
            db.ReceivableEntries.AddRange(customerIds.Select((id, index) => new ReceivableEntryRecord
            {
                Id = Guid.NewGuid(), CustomerId = id, EntryType = "Charge", Amount = index == 0 ? 20m : 1m,
                Currency = "USD", Reason = "cargo de prueba", ActorId = Guid.NewGuid(), TerminalId = Guid.NewGuid(),
                OccurredAtUtc = now
            }));
            db.ReceivableEntries.AddRange(
                new ReceivableEntryRecord
                {
                    Id = Guid.NewGuid(), CustomerId = customerIds[0], EntryType = "Payment", Amount = 5m,
                    Currency = "USD", OriginalAmount = 4500m, OriginalCurrency = "VES", AppliedExchangeRate = 0.00111111m,
                    Reason = "abono de prueba", ActorId = Guid.NewGuid(), TerminalId = Guid.NewGuid(), OccurredAtUtc = now
                },
                new ReceivableEntryRecord
                {
                    Id = Guid.NewGuid(), CustomerId = customerIds[0], EntryType = "Charge", Amount = 10m,
                    Currency = "USD", OriginalAmount = 9000m, OriginalCurrency = "VES", AppliedExchangeRate = 0.00111111m,
                    Reason = "cargo dentro del período", ActorId = Guid.NewGuid(), TerminalId = Guid.NewGuid(), OccurredAtUtc = fromUtc
                },
                new ReceivableEntryRecord
                {
                    Id = Guid.NewGuid(), CustomerId = customerIds[0], EntryType = "Payment", Amount = 2m,
                    Currency = "USD", OriginalAmount = 1800m, OriginalCurrency = "VES", AppliedExchangeRate = 0.00111111m,
                    Reason = "abono dentro del período", ActorId = Guid.NewGuid(), TerminalId = Guid.NewGuid(), OccurredAtUtc = fromUtc.AddMinutes(30)
                },
                new ReceivableEntryRecord
                {
                    Id = Guid.NewGuid(), CustomerId = customerIds[0], EntryType = "Charge", Amount = 1m,
                    Currency = "USD", OriginalAmount = 1m, OriginalCurrency = "USD",
                    Reason = "cargo en el límite final", ActorId = Guid.NewGuid(), TerminalId = Guid.NewGuid(), OccurredAtUtc = toUtc
                });
            await db.SaveChangesAsync();

            var queries = new ReceivablesReportQueryService(db);
            var firstPage = await queries.GetBalancePageAsync(1, 40);
            var secondPage = await queries.GetBalancePageAsync(2, 40);
            var lastPage = await queries.GetBalancePageAsync(3, 40);
            var movements = await queries.GetMovementPageAsync(fromUtc, toUtc, "VES", 1, 20);

            Assert.Equal(101, firstPage.TotalCount);
            Assert.Equal(40, firstPage.Customers.Count);
            Assert.Equal(40, secondPage.Customers.Count);
            Assert.Equal(21, lastPage.Customers.Count);
            Assert.Equal(101, firstPage.Customers.Concat(secondPage.Customers).Concat(lastPage.Customers)
                .Select(item => item.CustomerId).Distinct().Count());
            var balance = firstPage.Customers.Concat(secondPage.Customers).Concat(lastPage.Customers)
                .Single(item => item.CustomerId == customerIds[0]);
            Assert.Equal(24m, balance.BalanceUsd);
            Assert.Equal(476m, balance.AvailableCreditUsd);
            Assert.Equal(21600m, balance.BalanceVesEquivalent);
            Assert.Equal(900m, firstPage.CurrentUsdToVesRate);
            Assert.Equal(2, movements.TotalCount);
            Assert.Equal(2, movements.Movements.Count);
            Assert.All(movements.Movements, item => Assert.Equal("VES", item.OriginalCurrency));
            Assert.DoesNotContain(movements.Movements, item => item.OccurredAtUtc == toUtc);
        }
        finally
        {
            await transaction.RollbackAsync();
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
