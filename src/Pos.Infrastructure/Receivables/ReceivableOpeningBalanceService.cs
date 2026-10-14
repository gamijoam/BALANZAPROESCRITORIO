using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Receivables;

public sealed class ReceivableOpeningBalanceService(PosDbContext db)
{
    public async Task<ReceivableOpeningBalanceResult> RecordAsync(Guid customerId, decimal amountUsd, string reason,
        string idempotencyKey, Guid actorId, Guid terminalId, CancellationToken cancellationToken = default)
    {
        reason = (reason ?? string.Empty).Trim();
        idempotencyKey = (idempotencyKey ?? string.Empty).Trim();
        if (amountUsd <= 0m || decimal.Round(amountUsd, 4, MidpointRounding.AwayFromZero) != amountUsd)
            throw new ReceivableOpeningBalanceException("invalid_opening_balance");
        if (reason.Length is < 3 or > 250)
            throw new ReceivableOpeningBalanceException("invalid_opening_balance_reason");
        if (idempotencyKey.Length is < 1 or > 128)
            throw new ReceivableOpeningBalanceException("invalid_idempotency_key");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var replay = await FindReplayAsync(customerId, amountUsd, reason, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ReceivableOpeningBalanceResult(replay.Id, replay.Amount, Replayed: true);
        }

        var customer = await db.Customers.FromSqlInterpolated(
            $"SELECT * FROM customers WHERE \"Id\" = {customerId} AND \"Active\" = TRUE FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (customer is null) throw new ReceivableOpeningBalanceException("customer_not_found");
        if (customer.Currency != "USD") throw new ReceivableOpeningBalanceException("customer_account_currency_invalid");

        // Recheck after taking the customer lock so a retry that waited for a concurrent request is replayed.
        replay = await FindReplayAsync(customerId, amountUsd, reason, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ReceivableOpeningBalanceResult(replay.Id, replay.Amount, Replayed: true);
        }

        if (await db.ReceivableEntries.AnyAsync(x => x.CustomerId == customerId && x.IsOpeningBalance, cancellationToken))
            throw new ReceivableOpeningBalanceException("opening_balance_already_exists");
        if (await db.ReceivableEntries.AnyAsync(x => x.CustomerId == customerId, cancellationToken))
            throw new ReceivableOpeningBalanceException("opening_balance_requires_empty_account");

        var now = DateTime.UtcNow;
        var entry = new ReceivableEntryRecord
        {
            Id = Guid.NewGuid(), CustomerId = customerId, EntryType = "Charge", Amount = amountUsd, Currency = "USD",
            OriginalAmount = amountUsd, OriginalCurrency = "USD", Reason = reason, IdempotencyKey = idempotencyKey,
            IsOpeningBalance = true, ActorId = actorId, TerminalId = terminalId, OccurredAtUtc = now
        };
        db.ReceivableEntries.Add(entry);
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = actorId, TerminalId = terminalId,
            Action = "receivable.opening_balance.recorded",
            Payload = JsonSerializer.Serialize(new { customerId, amountUsd, currency = "USD", reason, idempotencyKey }),
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReceivableOpeningBalanceResult(entry.Id, entry.Amount, Replayed: false);
    }

    private async Task<ReceivableEntryRecord?> FindReplayAsync(Guid customerId, decimal amountUsd, string reason,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await db.ReceivableEntries.SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is null) return null;
        if (existing.CustomerId != customerId || !existing.IsOpeningBalance || existing.Amount != amountUsd || existing.Reason != reason)
            throw new ReceivableOpeningBalanceException("idempotency_key_reused");
        return existing;
    }
}

public sealed record ReceivableOpeningBalanceResult(Guid EntryId, decimal Amount, bool Replayed);

public sealed class ReceivableOpeningBalanceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
