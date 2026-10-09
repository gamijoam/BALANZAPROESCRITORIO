using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sales;

public sealed class SalesService(PosDbContext db)
{
    public async Task<SaleResult> ConfirmAsync(Guid operatorId, Guid terminalId, Guid cashSessionId, string idempotencyKey,
        CheckoutRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.Lines is null || request.Payments is null || request.Lines.Any(x => x is null) || request.Payments.Any(x => x is null))
            throw new CheckoutRejectedException("invalid_checkout_shape");
        ValidateRequest(idempotencyKey, request);
        var payloadHash = ComputePayloadHash(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var prior = await db.IdempotencyRecords.SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.Key == idempotencyKey, cancellationToken);
        if (prior is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return prior.PayloadHash == payloadHash ? new SaleResult(prior.SaleId, true, null) : new SaleResult(Guid.Empty, false, "idempotency_conflict");
        }

        var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"OperatorId\" = {operatorId} AND \"TerminalId\" = {terminalId} AND \"Status\" = 'Open' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new CheckoutRejectedException("open_cash_session_required");
        var ids = request.Lines.Select(x => x.ProductId).Distinct().ToArray();
        if (ids.Length != request.Lines.Count) throw new CheckoutRejectedException("duplicate_product_line");
        var products = await db.Products.Where(x => ids.Contains(x.Id) && x.Active).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (products.Count != ids.Length) throw new CheckoutRejectedException("product_unavailable");

        var currency = cashSession.Currency;
        if (currency is not ("USD" or "VES")) throw new CheckoutRejectedException("unsupported_cash_currency");
        if (products.Values.Any(x => x.Currency is not ("USD" or "VES"))) throw new CheckoutRejectedException("unsupported_product_currency");
        var needsConversion = products.Values.Any(x => x.Currency != currency) || request.Payments.Any(x => x.Currency != currency);
        var appliedRate = needsConversion
            ? await db.ExchangeRates.Where(x => ((x.FromCurrency == "USD" && x.ToCurrency == "VES") || (x.FromCurrency == "VES" && x.ToCurrency == "USD")) && x.EffectiveAtUtc <= DateTime.UtcNow)
                .OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).FirstOrDefaultAsync(cancellationToken)
                ?? throw new CheckoutRejectedException("exchange_rate_unavailable")
            : null;
        if (needsConversion && request.ExchangeRateId != appliedRate!.Id)
            throw new CheckoutRejectedException("exchange_rate_changed");
        var usdToVes = appliedRate is null ? 1m : appliedRate.FromCurrency == "USD" ? appliedRate.Rate : 1m / appliedRate.Rate;
        var lineSnapshots = request.Lines.Select(line =>
        {
            if (line.Quantity <= 0 || decimal.Round(line.Quantity, 3) != line.Quantity) throw new CheckoutRejectedException("invalid_quantity");
            var product = products[line.ProductId];
            if (product.UnitPrice <= 0) throw new CheckoutRejectedException("invalid_product_price");
            var originalCurrency = product.Currency;
            var conversionRate = originalCurrency == currency ? 1m : originalCurrency == "USD" ? usdToVes : 1m / usdToVes;
            var unitPrice = decimal.Round(product.UnitPrice * conversionRate, 2, MidpointRounding.AwayFromZero);
            var amount = decimal.Round(unitPrice * line.Quantity, 2, MidpointRounding.AwayFromZero);
            return new SaleLineRecord
            {
                Id = Guid.NewGuid(), ProductId = product.Id, Sku = product.Sku, Description = product.Name,
                Quantity = line.Quantity, UnitPrice = unitPrice, LineTotal = amount,
                OriginalUnitPrice = product.UnitPrice, OriginalCurrency = originalCurrency,
                ExchangeRateId = originalCurrency == currency ? null : appliedRate!.Id,
                AppliedExchangeRate = originalCurrency == currency ? null : conversionRate
            };
        }).ToArray();
        var total = lineSnapshots.Sum(x => x.LineTotal);
        if (request.Payments.Count == 0 || request.Payments.Any(x => x.Amount <= 0 || x.Method is null || x.Method.Length is < 2 or > 32 || x.Currency is not ("USD" or "VES")) ||
            request.Payments.Any(x => IsCreditMethod(x.Method) && x.Currency != currency) ||
            request.Payments.Sum(x => x.Currency == currency ? x.Amount : decimal.Round(x.Currency == "USD" ? x.Amount * usdToVes : x.Amount / usdToVes, 2, MidpointRounding.AwayFromZero)) != total)
            throw new CheckoutRejectedException("payment_total_mismatch");
        var creditAmount = request.Payments.Where(x => IsCreditMethod(x.Method)).Sum(x => x.Amount);
        CustomerRecord? customer = null;
        if (creditAmount > 0m)
        {
            if (request.CustomerId is null || request.CustomerId == Guid.Empty) throw new CheckoutRejectedException("credit_customer_required");
            customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE \"Id\" = {request.CustomerId.Value} AND \"Active\" = TRUE FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
                ?? throw new CheckoutRejectedException("customer_unavailable");
            if (customer.Currency != currency) throw new CheckoutRejectedException("currency_mismatch");
            var balance = await db.ReceivableEntries.Where(x => x.CustomerId == customer.Id)
                .SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount, cancellationToken);
            if (balance + creditAmount > customer.CreditLimit) throw new CheckoutRejectedException("credit_limit_exceeded");
        }

        var saleId = Guid.NewGuid();
        var sale = new SaleRecord { Id = saleId, OperatorId = operatorId, TerminalId = terminalId, CashSessionId = cashSessionId, CustomerId = request.CustomerId, Total = total, Currency = currency, Status = "Confirmed", CreatedAtUtc = DateTime.UtcNow };
        db.Sales.Add(sale);
        db.IdempotencyRecords.Add(new IdempotencyRecord { Id = Guid.NewGuid(), TerminalId = terminalId, Key = idempotencyKey, PayloadHash = payloadHash, SaleId = saleId, CreatedAtUtc = DateTime.UtcNow });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var concurrent = await db.IdempotencyRecords.SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.Key == idempotencyKey, cancellationToken);
            if (concurrent is null) throw;
            return concurrent.PayloadHash == payloadHash ? new SaleResult(concurrent.SaleId, true, null) : new SaleResult(Guid.Empty, false, "idempotency_conflict");
        }

        foreach (var line in request.Lines)
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stocks SET \"Quantity\" = \"Quantity\" - {line.Quantity}, \"Version\" = \"Version\" + 1 WHERE \"ProductId\" = {line.ProductId} AND \"Quantity\" >= {line.Quantity}", cancellationToken);
            if (affected != 1) throw new CheckoutRejectedException("insufficient_stock");
        }
        foreach (var line in lineSnapshots) line.SaleId = saleId;
        db.SaleLines.AddRange(lineSnapshots);
        db.Payments.AddRange(request.Payments.Select(x => new PaymentRecord
        {
            Id = Guid.NewGuid(), SaleId = saleId, Method = x.Method, Amount = x.Amount, Currency = x.Currency,
            ExchangeRateId = x.Currency == currency ? null : appliedRate!.Id,
            AppliedExchangeRate = x.Currency == currency ? null : x.Currency == "USD" ? usdToVes : 1m / usdToVes
        }));
        if (creditAmount > 0m)
            db.ReceivableEntries.Add(new ReceivableEntryRecord { Id = Guid.NewGuid(), CustomerId = customer!.Id, SaleId = saleId, EntryType = "Charge", Amount = creditAmount, Currency = currency, Reason = $"Sale {saleId:N}", ActorId = operatorId, TerminalId = terminalId, OccurredAtUtc = DateTime.UtcNow });
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = operatorId, TerminalId = terminalId, Action = "sale.confirmed",
            Payload = JsonSerializer.Serialize(new
            {
                saleId, total, currency, exchangeRateId = appliedRate?.Id, usdToVesRate = appliedRate is null ? (decimal?)null : usdToVes,
                lines = lineSnapshots.Select(x => new { x.ProductId, x.OriginalUnitPrice, x.OriginalCurrency, x.UnitPrice, x.LineTotal, x.AppliedExchangeRate, x.ExchangeRateId })
            }), OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SaleResult(saleId, false, null);
    }

    private static void ValidateRequest(string key, CheckoutRequest request)
    {
        if (key.Length is < 8 or > 128 || key.Any(char.IsControl)) throw new CheckoutRejectedException("invalid_idempotency_key");
        if (request.Lines.Count is < 1 or > 100 || request.Payments.Count is < 1 or > 10) throw new CheckoutRejectedException("invalid_checkout_shape");
    }

    private static string ComputePayloadHash(CheckoutRequest request)
    {
        var canonical = new
        {
            request.CustomerId,
            request.ExchangeRateId,
            lines = request.Lines.OrderBy(x => x.ProductId).Select(x => new { x.ProductId, x.Quantity }),
            payments = request.Payments.OrderBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal).ThenBy(x => x.Amount).Select(x => new { x.Method, x.Amount, x.Currency })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }

    private static bool IsCreditMethod(string method) => string.Equals(method, "credit", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "fiado", StringComparison.OrdinalIgnoreCase);
}

public sealed record CheckoutRequest(IReadOnlyList<CheckoutLine> Lines, IReadOnlyList<CheckoutPayment> Payments, Guid? CustomerId = null, Guid? ExchangeRateId = null);
public sealed record CheckoutLine(Guid ProductId, decimal Quantity);
public sealed record CheckoutPayment(string Method, decimal Amount, string Currency);
public sealed record SaleResult(Guid SaleId, bool Replayed, string? Error);
public sealed class CheckoutRejectedException(string code) : Exception(code) { public string Code { get; } = code; }
