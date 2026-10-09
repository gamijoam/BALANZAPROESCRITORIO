namespace Pos.Domain.Identity;

/// <summary>Single-use authorization bound to one permission and one operation.</summary>
public sealed class SupervisorApproval
{
    private int _consumed;

    public Permission Permission { get; }
    public Guid ApproverId { get; }
    public Guid OperationId { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    private SupervisorApproval(Permission permission, Guid approverId, Guid operationId, DateTimeOffset expiresAtUtc)
    {
        Permission = permission;
        ApproverId = approverId;
        OperationId = operationId;
        ExpiresAtUtc = expiresAtUtc;
    }

    public static SupervisorApproval Issue(Permission permission, Guid approverId, Guid operationId, DateTimeOffset expiresAtUtc)
    {
        if (!Enum.IsDefined(permission)) throw new ArgumentOutOfRangeException(nameof(permission));
        if (approverId == Guid.Empty) throw new ArgumentException("Approver is required.", nameof(approverId));
        if (operationId == Guid.Empty) throw new ArgumentException("Operation is required.", nameof(operationId));
        if (expiresAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Expiry must be UTC.", nameof(expiresAtUtc));
        return new SupervisorApproval(permission, approverId, operationId, expiresAtUtc);
    }

    public bool TryConsume(Permission permission, Guid operationId, DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Current time must be UTC.", nameof(nowUtc));
        if (nowUtc >= ExpiresAtUtc || permission != Permission || operationId != OperationId)
            return false;
        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }
}
