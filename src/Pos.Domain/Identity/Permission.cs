namespace Pos.Domain.Identity;

public enum Permission
{
    ConfirmSale,
    ApplyDiscount,
    ChangePrice,
    AdjustStock,
    ReturnSale,
    ManageUsers,
    ManageExchangeRate,
    CloseCashSession,
    ViewSensitiveReports,
    ManageCatalog,
    ManageCustomers,
    ManageReceivables,
    ViewReports,
    ViewExchangeRate
}

public enum OperatorRole { Cashier, Supervisor, Administrator }

public static class RolePermissionPolicy
{
    public static PermissionSet For(OperatorRole role) => role switch
    {
        OperatorRole.Cashier => new PermissionSet(Permission.ConfirmSale, Permission.CloseCashSession, Permission.ViewExchangeRate),
        OperatorRole.Supervisor => new PermissionSet(
            Permission.ConfirmSale, Permission.ApplyDiscount, Permission.ChangePrice, Permission.AdjustStock,
            Permission.ReturnSale, Permission.ManageExchangeRate, Permission.CloseCashSession,
            Permission.ViewSensitiveReports, Permission.ManageCatalog, Permission.ManageCustomers,
            Permission.ManageReceivables, Permission.ViewReports, Permission.ViewExchangeRate),
        OperatorRole.Administrator => new PermissionSet(Enum.GetValues<Permission>()),
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}

public sealed class PermissionSet
{
    private readonly HashSet<Permission> _permissions;

    public PermissionSet(IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Any(permission => !Enum.IsDefined(permission)))
            throw new ArgumentOutOfRangeException(nameof(permissions), "Permission set contains an unknown permission.");
        _permissions = new HashSet<Permission>(permissions);
    }

    public PermissionSet(params Permission[] permissions) : this((IEnumerable<Permission>)permissions) { }

    public bool Can(Permission permission) => _permissions.Contains(permission);
}
