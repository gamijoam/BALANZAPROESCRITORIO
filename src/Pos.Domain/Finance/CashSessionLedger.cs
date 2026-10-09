namespace Pos.Domain.Finance;

public sealed record CashReceipt(string Currency, decimal Amount);
public sealed record CashMovement(string Currency, string Direction, decimal Amount);
public sealed record DualCashReconciliation(CashReconciliation Usd, CashReconciliation Ves);

public static class CashSessionLedger
{
    public static DualCashReconciliation Calculate(decimal openingUsd, decimal openingVes,
        IEnumerable<CashReceipt> receipts, IEnumerable<CashMovement> movements,
        decimal countedUsd, decimal countedVes)
    {
        var receiptList = receipts.ToArray();
        var movementList = movements.ToArray();
        Validate(openingUsd, openingVes, receiptList, movementList, countedUsd, countedVes);
        return new DualCashReconciliation(
            Reconcile("USD", openingUsd, receiptList, movementList, countedUsd),
            Reconcile("VES", openingVes, receiptList, movementList, countedVes));
    }

    public static decimal ExpectedBalance(decimal opening, IEnumerable<CashReceipt> receipts,
        IEnumerable<CashMovement> movements, string currency)
    {
        if (opening < 0m || currency is not ("USD" or "VES")) throw new ArgumentOutOfRangeException(nameof(opening));
        var receiptList = receipts.ToArray(); var movementList = movements.ToArray();
        ValidateEntries(receiptList, movementList);
        return opening
            + receiptList.Where(x => x.Currency == currency).Sum(x => x.Amount)
            + movementList.Where(x => x.Currency == currency && x.Direction == "In").Sum(x => x.Amount)
            - movementList.Where(x => x.Currency == currency && x.Direction == "Out").Sum(x => x.Amount);
    }

    private static CashReconciliation Reconcile(string currency, decimal opening,
        IReadOnlyCollection<CashReceipt> receipts, IReadOnlyCollection<CashMovement> movements, decimal counted)
        => CashReconciliation.Calculate(opening,
            receipts.Where(x => x.Currency == currency).Select(x => x.Amount)
                .Concat(movements.Where(x => x.Currency == currency && x.Direction == "In").Select(x => x.Amount)),
            movements.Where(x => x.Currency == currency && x.Direction == "Out").Select(x => x.Amount), counted);

    private static void Validate(decimal openingUsd, decimal openingVes, IReadOnlyCollection<CashReceipt> receipts,
        IReadOnlyCollection<CashMovement> movements, decimal countedUsd, decimal countedVes)
    {
        if (openingUsd < 0m || openingVes < 0m || countedUsd < 0m || countedVes < 0m)
            throw new ArgumentOutOfRangeException(nameof(openingUsd), "Los saldos de caja no pueden ser negativos.");
        ValidateEntries(receipts, movements);
    }

    private static void ValidateEntries(IReadOnlyCollection<CashReceipt> receipts, IReadOnlyCollection<CashMovement> movements)
    {
        if (receipts.Any(x => x.Currency is not ("USD" or "VES") || x.Amount < 0m) ||
            movements.Any(x => x.Currency is not ("USD" or "VES") || x.Direction is not ("In" or "Out") || x.Amount < 0m))
            throw new ArgumentException("Los cobros y movimientos deben tener moneda y dirección válidas.");
    }
}
