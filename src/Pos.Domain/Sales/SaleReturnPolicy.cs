using Pos.Domain;

namespace Pos.Domain.Sales;

public static class SaleReturnPolicy
{
    public static IReadOnlyList<ValidatedSaleReturnLine> Validate(
        IEnumerable<OriginalSaleLine> originalLines,
        IEnumerable<ReturnedSaleLine> previousReturns,
        IEnumerable<RequestedSaleReturnLine> requestedLines)
    {
        ArgumentNullException.ThrowIfNull(originalLines);
        ArgumentNullException.ThrowIfNull(previousReturns);
        ArgumentNullException.ThrowIfNull(requestedLines);

        var original = originalLines.ToDictionary(x => x.SaleLineId);
        var prior = previousReturns.ToArray();
        var requested = requestedLines.ToArray();
        if (original.Count == 0 || original.Values.Any(x => x.SaleLineId == Guid.Empty || x.Quantity <= 0m || decimal.Round(x.Quantity, 3) != x.Quantity || x.UnitPrice <= 0m))
            throw new DomainRuleException("La venta original no contiene artículos válidos para devolver.");
        if (prior.Any(x => x.SaleLineId == Guid.Empty || x.Quantity <= 0m || decimal.Round(x.Quantity, 3) != x.Quantity))
            throw new DomainRuleException("El historial de devoluciones contiene cantidades inválidas.");
        if (requested.Length == 0 || requested.Any(x => x.SaleLineId == Guid.Empty || x.Quantity <= 0m || decimal.Round(x.Quantity, 3) != x.Quantity))
            throw new DomainRuleException("Indica artículos y cantidades válidas para devolver.");
        if (requested.Select(x => x.SaleLineId).Distinct().Count() != requested.Length)
            throw new DomainRuleException("Un artículo no puede aparecer más de una vez en la misma devolución.");

        var previouslyReturned = prior.GroupBy(x => x.SaleLineId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var result = new List<ValidatedSaleReturnLine>(requested.Length);
        foreach (var item in requested)
        {
            if (!original.TryGetValue(item.SaleLineId, out var saleLine))
                throw new DomainRuleException("El artículo no pertenece a la venta indicada.");

            var alreadyReturned = previouslyReturned.GetValueOrDefault(item.SaleLineId);
            if (alreadyReturned + item.Quantity > saleLine.Quantity)
                throw new DomainRuleException("La cantidad devuelta supera la cantidad vendida pendiente de devolución.");

            var refundAmount = decimal.Round(saleLine.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
            if (refundAmount <= 0m)
                throw new DomainRuleException("El importe de devolución debe ser mayor que cero.");

            result.Add(new ValidatedSaleReturnLine(item.SaleLineId, item.Quantity, refundAmount, item.Restock));
        }

        return result;
    }
}

public sealed record OriginalSaleLine(Guid SaleLineId, decimal Quantity, decimal UnitPrice);
public sealed record ReturnedSaleLine(Guid SaleLineId, decimal Quantity);
public sealed record RequestedSaleReturnLine(Guid SaleLineId, decimal Quantity, bool Restock);
public sealed record ValidatedSaleReturnLine(Guid SaleLineId, decimal Quantity, decimal RefundAmount, bool Restock);
