using Microsoft.EntityFrameworkCore;
using Pos.Domain.Finance;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Contracts;

namespace Pos.Infrastructure.Reporting;

/// <summary>Read-only, paged queries used by the consolidated receivables reports.</summary>
public sealed class ReceivablesReportQueryService(PosDbContext db)
{
    private const int MaxPageSize = 250;

    public async Task<ReceivableBalanceReportPage> GetBalancePageAsync(int page = 1, int pageSize = 250,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Clamp(page, 1, int.MaxValue / pageSize);
        var balances = db.Customers.AsNoTracking().Where(customer => customer.Active)
            .Select(customer => new
            {
                customer.Id,
                customer.Name,
                customer.CreditLimit,
                Balance = db.ReceivableEntries.Where(entry => entry.CustomerId == customer.Id)
                    .Sum(entry => entry.EntryType == "Charge" ? entry.Amount : -entry.Amount),
                LastMovementAtUtc = db.ReceivableEntries.Where(entry => entry.CustomerId == customer.Id)
                    .Max(entry => (DateTime?)entry.OccurredAtUtc)
            })
            .Where(customer => customer.Balance > 0m);

        var totalCount = await balances.CountAsync(cancellationToken);
        var customers = await balances.OrderBy(customer => customer.Name).ThenBy(customer => customer.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var rate = await CurrentUsdToVesRateAsync(DateTime.UtcNow, cancellationToken);
        var rows = customers.Select(customer => new ReceivableBalanceReportRow(customer.Id, customer.Name,
            customer.CreditLimit, customer.Balance, Math.Max(0m, customer.CreditLimit - customer.Balance),
            rate is null ? null : CreditAccountCurrency.ToVes(customer.Balance, rate.Value), customer.LastMovementAtUtc))
            .ToArray();

        return new ReceivableBalanceReportPage(page, pageSize, totalCount, rate, rows);
    }

    public async Task<ReceivableMovementReportPage> GetMovementPageAsync(DateTime fromUtc, DateTime toUtc,
        string? currency, int page = 1, int pageSize = 250, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Clamp(page, 1, int.MaxValue / pageSize);
        var entries = from entry in db.ReceivableEntries.AsNoTracking()
                      join customer in db.Customers.AsNoTracking() on entry.CustomerId equals customer.Id
                      join user in db.OperatorUsers.AsNoTracking() on entry.ActorId equals user.Id into users
                      from user in users.DefaultIfEmpty()
                      where entry.OccurredAtUtc >= fromUtc && entry.OccurredAtUtc < toUtc
                      select new { Entry = entry, Customer = customer, OperatorName = user == null ? null : user.Username };

        if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency, "Todas", StringComparison.OrdinalIgnoreCase))
        {
            currency = currency.Trim().ToUpperInvariant();
            entries = entries.Where(row => (row.Entry.OriginalCurrency ?? row.Entry.Currency) == currency);
        }

        var totalCount = await entries.CountAsync(cancellationToken);
        var rows = await entries.OrderByDescending(row => row.Entry.OccurredAtUtc).ThenByDescending(row => row.Entry.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(row => new ReceivableMovementReportRow(row.Entry.Id, row.Entry.CustomerId, row.Customer.Name,
                row.Entry.SaleId, row.Entry.EntryType, row.Entry.Amount, row.Entry.OriginalAmount,
                row.Entry.OriginalCurrency ?? row.Entry.Currency, row.Entry.AppliedExchangeRate,
                row.Entry.Reason, row.OperatorName ?? "—", row.Entry.TerminalId, row.Entry.OccurredAtUtc))
            .ToListAsync(cancellationToken);

        return new ReceivableMovementReportPage(page, pageSize, totalCount, rows);
    }

    private async Task<decimal?> CurrentUsdToVesRateAsync(DateTime atUtc, CancellationToken cancellationToken)
    {
        var rate = await db.ExchangeRates.AsNoTracking()
            .Where(row => ((row.FromCurrency == "USD" && row.ToCurrency == "VES") ||
                           (row.FromCurrency == "VES" && row.ToCurrency == "USD")) && row.EffectiveAtUtc <= atUtc)
            .OrderByDescending(row => row.EffectiveAtUtc)
            .ThenBy(row => row.FromCurrency == "USD" ? 0 : 1)
            .Select(row => new { row.FromCurrency, row.Rate })
            .FirstOrDefaultAsync(cancellationToken);
        if (rate is null || rate.Rate <= 0m) return null;
        return rate.FromCurrency == "USD" ? rate.Rate : 1m / rate.Rate;
    }
}
