using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Domain;
using Pos.Domain.Finance;
using Pos.Domain.Sales;
using Pos.Infrastructure.Modules;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sales;

public sealed class SaleReturnService(PosDbContext db)
{
    public async Task<SaleReturnResult> ReturnAsync(Guid operatorId, Guid terminalId, Guid cashSessionId, Guid saleId,
        string idempotencyKey, SaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.Lines is null || request.Lines.Any(x => x is null))
            throw new CheckoutRejectedException("invalid_return_shape");
        if (idempotencyKey.Length is < 8 or > 128 || idempotencyKey.Any(char.IsControl))
            throw new CheckoutRejectedException("invalid_idempotency_key");
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < 3 or > 250) throw new CheckoutRejectedException("return_reason_required");
        var payloadHash = ComputePayloadHash(saleId, cashSessionId, request with { Reason = reason });

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var priorRequest = await db.ReturnIdempotencyRecords.SingleOrDefaultAsync(
            x => x.TerminalId == terminalId && x.Key == idempotencyKey, cancellationToken);
        if (priorRequest is not null)
        {
            var priorReturn = await db.SaleReturns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == priorRequest.SaleReturnId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return priorRequest.PayloadHash == payloadHash
                ? new SaleReturnResult(priorRequest.SaleReturnId, true, null, priorReturn?.Amount ?? 0m, priorReturn?.Currency ?? string.Empty)
                : new SaleReturnResult(Guid.Empty, false, "idempotency_conflict", 0m, string.Empty);
        }

        var sale = await db.Sales.FromSqlInterpolated($"SELECT * FROM sales WHERE \"Id\" = {saleId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw new CheckoutRejectedException("sale_not_found");
        if (sale.Status is not ("Confirmed" or "PartiallyReturned")) throw new CheckoutRejectedException("sale_not_returnable");

        var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"ActiveOperatorId\" = {operatorId} AND \"TerminalId\" = {terminalId} AND \"Status\" = 'Open' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw new CheckoutRejectedException("open_cash_session_required");
        var saleLines = await db.SaleLines.Where(x => x.SaleId == saleId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var returnedLines = await (from line in db.SaleReturnLines
                                   join priorHeader in db.SaleReturns on line.SaleReturnId equals priorHeader.Id
                                   where priorHeader.SaleId == saleId
                                   select line).ToListAsync(cancellationToken);
        IReadOnlyList<ValidatedSaleReturnLine> validated;
        try
        {
            validated = SaleReturnPolicy.Validate(
                saleLines.Select(x => new OriginalSaleLine(x.Id, x.Quantity, x.UnitPrice)),
                returnedLines.Select(x => new ReturnedSaleLine(x.SaleLineId, x.Quantity)),
                request.Lines.Select(x => new RequestedSaleReturnLine(x.SaleLineId, x.Quantity, x.Restock)));
        }
        catch (DomainRuleException)
        {
            throw new CheckoutRejectedException("invalid_return_lines");
        }

        var returnAmount = validated.Sum(x => x.RefundAmount);
        var payments = await db.Payments.Where(x => x.SaleId == saleId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var paymentIds = payments.Select(x => x.Id).ToArray();
        var previousRefunds = await db.SaleReturnPayments.Where(x => paymentIds.Contains(x.OriginalPaymentId)).ToListAsync(cancellationToken);
        var allocations = AllocateRefund(returnAmount, sale.Currency, payments, previousRefunds);
        var creditCharge = await db.ReceivableEntries.SingleOrDefaultAsync(x => x.SaleId == saleId && x.EntryType == "Charge", cancellationToken);

        var cashReceipts = await (from payment in db.Payments
                                  join sourceSale in db.Sales on payment.SaleId equals sourceSale.Id
                                  where sourceSale.CashSessionId == cashSessionId &&
                                        (payment.Method.ToLower() == "cash" || payment.Method.ToLower() == "efectivo")
                                  select new CashReceipt(payment.Currency, payment.Amount)).ToListAsync(cancellationToken);
        var priorCashMovements = await db.CashMovements.Where(x => x.CashSessionId == cashSessionId)
            .Select(x => new CashMovement(x.Currency, x.Direction, x.Amount)).ToListAsync(cancellationToken);
        foreach (var allocation in allocations.Where(x => IsCashMethod(x.Method)))
        {
            var opening = OpeningBalance(cashSession, allocation.Currency);
            var expected = CashSessionLedger.ExpectedBalance(opening, cashReceipts, priorCashMovements, allocation.Currency);
            if (allocation.Amount > expected) throw new CheckoutRejectedException("insufficient_cash_for_refund");
            priorCashMovements.Add(new CashMovement(allocation.Currency, "Out", allocation.Amount));
        }

        var now = DateTime.UtcNow;
        var returnId = Guid.NewGuid();
        var header = new SaleReturnRecord
        {
            Id = returnId, SaleId = saleId, OperatorId = operatorId, TerminalId = terminalId, CashSessionId = cashSession.Id,
            Amount = returnAmount, Currency = sale.Currency, Reason = reason, CreatedAtUtc = now
        };
        db.SaleReturns.Add(header);
        db.SaleReturnLines.AddRange(validated.Select(item =>
        {
            var original = saleLines.Single(x => x.Id == item.SaleLineId);
            return new SaleReturnLineRecord
            {
                Id = Guid.NewGuid(), SaleReturnId = returnId, SaleLineId = original.Id, ProductId = original.ProductId,
                Quantity = item.Quantity, UnitPrice = original.UnitPrice, Amount = item.RefundAmount, Restock = item.Restock
            };
        }));

        foreach (var item in validated.Where(x => x.Restock))
        {
            var productId = saleLines.Single(x => x.Id == item.SaleLineId).ProductId;
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stocks SET \"Quantity\" = \"Quantity\" + {item.Quantity}, \"Version\" = \"Version\" + 1 WHERE \"ProductId\" = {productId}", cancellationToken);
            if (changed != 1) throw new CheckoutRejectedException("stock_record_missing");
            await WarehouseStockBridge.RestockSaleAsync(db, saleId, productId, item.Quantity, cancellationToken);
            var movementReason = $"Devolución {returnId:N}: {reason}";
            if (movementReason.Length > 250) movementReason = movementReason[..250];
            db.StockMovements.Add(new StockMovementRecord
            {
                Id = Guid.NewGuid(), ProductId = productId, QuantityDelta = item.Quantity,
                Reason = movementReason, ActorId = operatorId, TerminalId = terminalId, OccurredAtUtc = now
            });
        }

        foreach (var allocation in allocations)
        {
            var originalPayment = payments.Single(x => x.Id == allocation.OriginalPaymentId);
            db.SaleReturnPayments.Add(new SaleReturnPaymentRecord
            {
                Id = Guid.NewGuid(), SaleReturnId = returnId, OriginalPaymentId = originalPayment.Id,
                Method = allocation.Method, Amount = allocation.Amount, Currency = allocation.Currency,
                AppliedExchangeRate = allocation.AppliedExchangeRate
            });
            if (IsCashMethod(allocation.Method))
            {
                db.CashMovements.Add(new CashMovementRecord
                {
                    Id = Guid.NewGuid(), CashSessionId = cashSession.Id, Amount = allocation.Amount, Currency = allocation.Currency,
                    Direction = "Out", Reason = $"Reintegro devolución {returnId:N}", ActorId = operatorId, OccurredAtUtc = now
                });
            }
            else if (IsCreditMethod(allocation.Method))
            {
                if (sale.CustomerId is null || creditCharge is null) throw new CheckoutRejectedException("credit_charge_missing");
                var appliedRate = sale.Currency == "USD" ? 1m : creditCharge.AppliedExchangeRate
                    ?? throw new CheckoutRejectedException("credit_exchange_rate_missing");
                var amountUsd = decimal.Round(allocation.Amount * appliedRate, 2, MidpointRounding.AwayFromZero);
                db.ReceivableEntries.Add(new ReceivableEntryRecord
                {
                    Id = Guid.NewGuid(), CustomerId = sale.CustomerId.Value, SaleId = saleId, EntryType = "Payment",
                    Amount = amountUsd, Currency = "USD", OriginalAmount = allocation.Amount,
                    OriginalCurrency = sale.Currency, AppliedExchangeRate = sale.Currency == "USD" ? null : appliedRate,
                    Reason = $"Devolución {returnId:N}", ActorId = operatorId, TerminalId = terminalId, OccurredAtUtc = now
                });
            }
        }

        var updatedReturned = returnedLines.GroupBy(x => x.SaleLineId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        foreach (var item in validated) updatedReturned[item.SaleLineId] = updatedReturned.GetValueOrDefault(item.SaleLineId) + item.Quantity;
        sale.Status = saleLines.All(x => updatedReturned.GetValueOrDefault(x.Id) >= x.Quantity) ? "Returned" : "PartiallyReturned";
        db.ReturnIdempotencyRecords.Add(new ReturnIdempotencyRecord
        {
            Id = Guid.NewGuid(), TerminalId = terminalId, Key = idempotencyKey, PayloadHash = payloadHash,
            SaleReturnId = returnId, CreatedAtUtc = now
        });
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = operatorId, TerminalId = terminalId, Action = "sale.returned",
            Payload = JsonSerializer.Serialize(new { saleId, returnId, returnAmount, currency = sale.Currency, reason,
                lines = validated.Select(x => new { x.SaleLineId, x.Quantity, x.RefundAmount, x.Restock }),
                payments = allocations.Select(x => new { x.Method, x.Amount, x.Currency, x.AppliedExchangeRate }) }), OccurredAtUtc = now
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var concurrent = await db.ReturnIdempotencyRecords.SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.Key == idempotencyKey, cancellationToken);
            if (concurrent is null) throw;
            var concurrentReturn = await db.SaleReturns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == concurrent.SaleReturnId, cancellationToken);
            return concurrent.PayloadHash == payloadHash
                ? new SaleReturnResult(concurrent.SaleReturnId, true, null, concurrentReturn?.Amount ?? 0m, concurrentReturn?.Currency ?? string.Empty)
                : new SaleReturnResult(Guid.Empty, false, "idempotency_conflict", 0m, string.Empty);
        }

        return new SaleReturnResult(returnId, false, null, returnAmount, sale.Currency);
    }

    private static IReadOnlyList<RefundAllocation> AllocateRefund(decimal amount, string saleCurrency,
        IReadOnlyList<PaymentRecord> payments, IReadOnlyCollection<SaleReturnPaymentRecord> priorRefunds)
    {
        if (payments.Count == 0) throw new CheckoutRejectedException("sale_payments_missing");
        var rows = payments.Select(payment =>
        {
            var factor = payment.Currency == saleCurrency ? 1m : payment.AppliedExchangeRate
                ?? throw new CheckoutRejectedException("original_payment_rate_missing");
            if (factor <= 0m) throw new CheckoutRejectedException("original_payment_rate_invalid");
            var refunded = priorRefunds.Where(x => x.OriginalPaymentId == payment.Id).Sum(x => x.Amount);
            var remaining = payment.Amount - refunded;
            if (remaining < 0m) throw new CheckoutRejectedException("original_payment_over_refunded");
            return new { Payment = payment, Factor = factor, Remaining = remaining, RemainingSaleAmount = decimal.Round(remaining * factor, 2, MidpointRounding.AwayFromZero) };
        }).Where(x => x.Remaining > 0m).ToArray();
        var available = rows.Sum(x => x.RemainingSaleAmount);
        if (amount <= 0m || available < amount) throw new CheckoutRejectedException("refund_exceeds_original_payments");

        var allocations = new List<RefundAllocation>();
        var remainingSaleAmount = amount;
        var remainingWeight = available;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var targetSaleAmount = index == rows.Length - 1
                ? remainingSaleAmount
                : decimal.Round(remainingSaleAmount * row.RemainingSaleAmount / remainingWeight, 2, MidpointRounding.AwayFromZero);
            targetSaleAmount = Math.Min(targetSaleAmount, row.RemainingSaleAmount);
            var tenderAmount = index == rows.Length - 1 && row.Factor == 1m
                ? targetSaleAmount
                : decimal.Round(targetSaleAmount / row.Factor, 2, MidpointRounding.AwayFromZero);
            tenderAmount = Math.Min(tenderAmount, row.Remaining);
            var converted = decimal.Round(tenderAmount * row.Factor, 2, MidpointRounding.AwayFromZero);
            if (tenderAmount > 0m)
            {
                allocations.Add(new RefundAllocation(row.Payment.Id, row.Payment.Method, tenderAmount, row.Payment.Currency,
                    row.Payment.AppliedExchangeRate));
                remainingSaleAmount -= converted;
            }
            remainingWeight -= row.RemainingSaleAmount;
        }

        var allocatedSaleAmount = allocations.Sum(x =>
        {
            var original = payments.Single(p => p.Id == x.OriginalPaymentId);
            return decimal.Round(x.Amount * (original.Currency == saleCurrency ? 1m : original.AppliedExchangeRate!.Value), 2, MidpointRounding.AwayFromZero);
        });
        if (Math.Abs(allocatedSaleAmount - amount) > 0.01m)
            throw new CheckoutRejectedException("refund_rounding_conflict");
        return allocations;
    }

    private static decimal OpeningBalance(CashSessionRecord session, string currency) => currency == "USD"
        ? session.OpeningBalanceUsd == 0m && session.Currency == "USD" ? session.OpeningBalance : session.OpeningBalanceUsd
        : session.OpeningBalanceVes == 0m && session.Currency == "VES" ? session.OpeningBalance : session.OpeningBalanceVes;

    private static string ComputePayloadHash(Guid saleId, Guid cashSessionId, SaleReturnRequest request)
    {
        var canonical = new
        {
            saleId,
            cashSessionId,
            request.Reason,
            lines = request.Lines.OrderBy(x => x.SaleLineId).Select(x => new { x.SaleLineId, x.Quantity, x.Restock })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }

    private static bool IsCashMethod(string method) => string.Equals(method, "cash", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "efectivo", StringComparison.OrdinalIgnoreCase);
    private static bool IsCreditMethod(string method) => string.Equals(method, "credit", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "fiado", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "credito", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "crédito", StringComparison.OrdinalIgnoreCase);
}

public sealed record SaleReturnRequest(string Reason, IReadOnlyList<SaleReturnLineRequest> Lines);
public sealed record SaleReturnLineRequest(Guid SaleLineId, decimal Quantity, bool Restock);
public sealed record SaleReturnResult(Guid ReturnId, bool Replayed, string? Error, decimal Amount, string Currency);
public sealed record RefundAllocation(Guid OriginalPaymentId, string Method, decimal Amount, string Currency, decimal? AppliedExchangeRate);
