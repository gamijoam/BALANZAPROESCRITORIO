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

    public async Task<ReceivableAgingReportPage> GetAgingPageAsync(DateOnly asOfDate, int page = 1, int pageSize = 250,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Clamp(page, 1, int.MaxValue / pageSize);
        var customers = await db.Customers.AsNoTracking()
            .OrderBy(customer => customer.Name).ThenBy(customer => customer.Id)
            .Select(customer => new { customer.Id, customer.Name })
            .ToListAsync(cancellationToken);
        var entries = await db.ReceivableEntries.AsNoTracking()
            .OrderBy(entry => entry.CustomerId).ThenBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.EntryType == "Charge" ? 0 : 1).ThenBy(entry => entry.Id)
            .Select(entry => new AgingEntry(entry.CustomerId, entry.EntryType, entry.Amount, entry.DueDate, entry.OccurredAtUtc, entry.Id))
            .ToListAsync(cancellationToken);
        var entriesByCustomer = entries.GroupBy(entry => entry.CustomerId).ToDictionary(group => group.Key, group => group.ToArray());
        var rows = new List<ReceivableAgingReportRow>();
        foreach (var customer in customers)
        {
            if (!entriesByCustomer.TryGetValue(customer.Id, out var customerEntries)) continue;
            var openCharges = new List<OpenCharge>();
            var firstOpenCharge = 0;
            var advanceCredit = 0m;
            foreach (var entry in customerEntries)
            {
                var remaining = Math.Max(0m, entry.Amount);
                if (entry.EntryType == "Charge")
                {
                    var appliedAdvance = Math.Min(advanceCredit, remaining);
                    advanceCredit -= appliedAdvance;
                    remaining -= appliedAdvance;
                    if (remaining > 0m) openCharges.Add(new OpenCharge(entry.DueDate, remaining));
                    continue;
                }

                while (remaining > 0m && firstOpenCharge < openCharges.Count)
                {
                    var charge = openCharges[firstOpenCharge];
                    var appliedPayment = Math.Min(charge.Remaining, remaining);
                    charge.Remaining -= appliedPayment;
                    remaining -= appliedPayment;
                    if (charge.Remaining == 0m) firstOpenCharge++;
                }
                advanceCredit += remaining;
            }

            decimal notDue = 0m, overdue1To30 = 0m, overdue31To60 = 0m, overdue61To90 = 0m, overdueOver90 = 0m, noDueDate = 0m;
            foreach (var charge in openCharges)
            {
                if (charge.Remaining <= 0m) continue;
                if (charge.DueDate is null) noDueDate += charge.Remaining;
                else
                {
                    var overdueDays = asOfDate.DayNumber - charge.DueDate.Value.DayNumber;
                    if (overdueDays <= 0) notDue += charge.Remaining;
                    else if (overdueDays <= 30) overdue1To30 += charge.Remaining;
                    else if (overdueDays <= 60) overdue31To60 += charge.Remaining;
                    else if (overdueDays <= 90) overdue61To90 += charge.Remaining;
                    else overdueOver90 += charge.Remaining;
                }
            }
            var balance = notDue + overdue1To30 + overdue31To60 + overdue61To90 + overdueOver90 + noDueDate;
            if (balance > 0m)
                rows.Add(new ReceivableAgingReportRow(customer.Id, customer.Name, notDue, overdue1To30,
                    overdue31To60, overdue61To90, overdueOver90, noDueDate, balance));
        }

        var orderedRows = rows.OrderByDescending(row => row.OverdueOver90Usd + row.Overdue61To90Usd + row.Overdue31To60Usd + row.Overdue1To30Usd)
            .ThenByDescending(row => row.BalanceUsd)
            .ThenBy(row => row.CustomerName, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.CustomerId).ToArray();
        return new ReceivableAgingReportPage(page, pageSize, orderedRows.Length, asOfDate,
            orderedRows.Skip((page - 1) * pageSize).Take(pageSize).ToArray());
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
            .OrderBy(row => row.FromCurrency == "USD" && row.ToCurrency == "VES" ? 0 : 1)
            .ThenByDescending(row => row.EffectiveAtUtc)
            .Select(row => new { row.FromCurrency, row.Rate })
            .FirstOrDefaultAsync(cancellationToken);
        if (rate is null || rate.Rate <= 0m) return null;
        return rate.FromCurrency == "USD" ? rate.Rate : 1m / rate.Rate;
    }

    private sealed record AgingEntry(Guid CustomerId, string EntryType, decimal Amount, DateOnly? DueDate, DateTime OccurredAtUtc, Guid Id);
    private sealed class OpenCharge(DateOnly? dueDate, decimal remaining)
    {
        public DateOnly? DueDate { get; } = dueDate;
        public decimal Remaining { get; set; } = remaining;
    }
}
