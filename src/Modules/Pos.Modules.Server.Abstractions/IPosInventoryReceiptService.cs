namespace Pos.Modules.Server.Abstractions;

/// <summary>Host-owned inventory boundary used by trusted server modules to record purchase receipts.</summary>
public interface IPosInventoryReceiptService
{
    /// <returns>True when this exact receipt was already applied; false when it was applied now.</returns>
    Task<bool> ApplyReceiptAsync(Guid receiptId, Guid? warehouseId,
        IReadOnlyList<PosInventoryReceiptLine> lines, Guid actorId, Guid terminalId,
        CancellationToken cancellationToken = default);

    /// <returns>True when this receipt had already been reversed; false when reversed now.</returns>
    Task<bool> ReverseReceiptAsync(Guid receiptId, Guid actorId, Guid terminalId,
        CancellationToken cancellationToken = default);
}

public sealed record PosInventoryReceiptLine(Guid ProductId, decimal Quantity, decimal UnitCost, string CostCurrency);
