namespace Pos.Modules.Finance;

/// <summary>Chooses the cost base for price suggestions without changing inventory valuation.</summary>
public static class FinancePriceSuggestionCostPolicy
{
    public const string WeightedAverage = "WeightedAverage";
    public const string LastReceivedCost = "LastReceivedCost";

    public const string LatestReceiptCostsSql = """
        WITH ranked_receipts AS (
            SELECT r.id AS receipt_id, r.invoice_id, r.completed_at_utc, i.document_number,
                   l.product_id,
                   ROW_NUMBER() OVER (
                       PARTITION BY l.product_id
                       ORDER BY r.completed_at_utc DESC, r.id DESC
                   ) AS receipt_rank
            FROM {purchase_receipts} r
            JOIN {purchase_receipt_lines} l ON l.receipt_id = r.id
            JOIN {purchase_invoices} i ON i.id = r.invoice_id
            WHERE l.product_id = ANY(@productIds)
              AND l.quantity > 0
              AND r.completed_at_utc IS NOT NULL
              AND r.reversed_at_utc IS NULL
            GROUP BY r.id, r.invoice_id, r.completed_at_utc, i.document_number, l.product_id
        )
        SELECT latest.product_id, latest.receipt_id, latest.document_number,
               latest.completed_at_utc, line.quantity, line.unit_cost,
               line.cost_currency, invoice.exchange_rate
        FROM ranked_receipts latest
        JOIN {purchase_receipt_lines} line ON line.receipt_id = latest.receipt_id
            AND line.product_id = latest.product_id
        JOIN {purchase_invoices} invoice ON invoice.id = latest.invoice_id
        WHERE latest.receipt_rank = 1
          AND line.quantity > 0
        ORDER BY latest.product_id, line.id
        """;

    public static string ResolveBasis(string? requestedBasis, string? storedBasis)
    {
        if (IsSupported(requestedBasis)) return requestedBasis!;
        return IsSupported(storedBasis) ? storedBasis! : WeightedAverage;
    }

    public static bool IsSupported(string? basis) => basis is WeightedAverage or LastReceivedCost;

    public static IReadOnlyDictionary<Guid, ReceivedCostSource> AggregateLatestReceiptLines(
        IEnumerable<ReceivedCostLine> lines, IReadOnlyDictionary<Guid, string> productCurrencies)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(productCurrencies);
        var result = new Dictionary<Guid, ReceivedCostSource>();
        foreach (var productLines in lines.Where(line => line.Quantity > 0m && line.UnitCost >= 0m &&
                     productCurrencies.ContainsKey(line.ProductId))
                 .GroupBy(line => line.ProductId))
        {
            var latestReceipt = productLines.GroupBy(line => line.ReceiptId)
                .Select(receiptLines =>
                {
                    var first = receiptLines.First();
                    var currency = productCurrencies[productLines.Key];
                    var quantity = 0m;
                    var totalCost = 0m;
                    foreach (var line in receiptLines)
                    {
                        var convertedCost = FinancePurchasePolicy.ConvertUnitCost(line.UnitCost,
                            line.CostCurrency, currency, line.UsdToVesRate);
                        quantity += line.Quantity;
                        totalCost += convertedCost * line.Quantity;
                    }
                    return new ReceivedCostSource(FinancePurchasePolicy.Round(totalCost / quantity),
                        first.ReceiptId, first.DocumentNumber, first.ReceivedAtUtc);
                })
                .OrderByDescending(source => source.ReceivedAtUtc)
                .ThenByDescending(source => source.ReceiptId)
                .First();
            result[productLines.Key] = latestReceipt;
        }
        return result;
    }

    public static SelectedPriceSuggestionCost Choose(string basis, decimal? weightedAverageCost,
        ReceivedCostSource? latestReceivedCost)
    {
        if (basis == WeightedAverage)
        {
            return weightedAverageCost is >= 0m
                ? new(basis, weightedAverageCost, null, null)
                : new(basis, null, null, "No hay un costo promedio válido para este artículo.");
        }

        if (basis == LastReceivedCost)
        {
            return latestReceivedCost is { UnitCost: >= 0m }
                ? new(basis, latestReceivedCost.UnitCost, latestReceivedCost, null)
                : new(basis, null, null, "No hay una recepción completada y vigente con un costo válido para este artículo.");
        }

        return new(basis, null, null, "El método de costo configurado no es válido.");
    }
}

public sealed record ReceivedCostSource(decimal UnitCost, Guid ReceiptId, string DocumentNumber, DateTime ReceivedAtUtc);

public sealed record ReceivedCostLine(Guid ProductId, Guid ReceiptId, string DocumentNumber, DateTime ReceivedAtUtc,
    decimal Quantity, decimal UnitCost, string CostCurrency, decimal UsdToVesRate);

public sealed record SelectedPriceSuggestionCost(string Basis, decimal? UnitCost,
    ReceivedCostSource? ReceiptSource, string? UnavailableReason);
