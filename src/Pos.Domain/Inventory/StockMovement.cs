namespace Pos.Domain.Inventory;

/// <summary>An immutable, attributable change to a product's stock balance.</summary>
public sealed record StockMovement
{
    public Guid ProductId { get; }
    public decimal QuantityDelta { get; }
    public string Reason { get; }
    public Guid ActorId { get; }
    public DateTimeOffset OccurredAtUtc { get; }

    private StockMovement(Guid productId, decimal quantityDelta, string reason, Guid actorId, DateTimeOffset occurredAtUtc)
    {
        ProductId = productId;
        QuantityDelta = quantityDelta;
        Reason = reason;
        ActorId = actorId;
        OccurredAtUtc = occurredAtUtc;
    }

    public static StockMovement Create(Guid productId, decimal quantityDelta, string reason, Guid actorId, DateTimeOffset occurredAtUtc)
    {
        if (productId == Guid.Empty) throw new ArgumentException("Product is required.", nameof(productId));
        if (quantityDelta == 0m) throw new ArgumentOutOfRangeException(nameof(quantityDelta));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 250) throw new ArgumentException("A reason of up to 250 characters is required.", nameof(reason));
        if (actorId == Guid.Empty) throw new ArgumentException("Actor is required.", nameof(actorId));
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Movement time must be UTC.", nameof(occurredAtUtc));
        return new StockMovement(productId, quantityDelta, reason.Trim(), actorId, occurredAtUtc);
    }
}
