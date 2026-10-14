namespace Pos.Domain.Identity;

public enum Permission
{
    ConfirmSale,
    CreateCreditSale,
    ApplyDiscount,
    ChangePrice,
    OverrideSalePrice,
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
    ViewExchangeRate,
    ManageModules,
    ViewWarehouses,
    ManageWarehouses,
    TransferInventory,
    ViewInventory,
    SelectPrice2,
    ViewSuppliers,
    ManageSuppliers,
    ViewPurchases,
    ManagePurchases,
    ManageSupplierPayments,
    ViewFinancialReports
}

public enum PermissionOverrideMode { Inherit, Allow, RequireAdminPin, Deny }
public enum PermissionAccessMode { Allow, RequireAdminPin, Deny }

public static class OperatorPermissionPolicy
{
    public static PermissionAccessMode Resolve(OperatorRole role, Permission permission, PermissionOverrideMode mode)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(permission)) throw new ArgumentOutOfRangeException(nameof(permission));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (role == OperatorRole.Administrator) return PermissionAccessMode.Allow;
        if (permission == Permission.ManageModules) return PermissionAccessMode.Deny;
        return mode switch
        {
            PermissionOverrideMode.Allow => PermissionAccessMode.Allow,
            PermissionOverrideMode.RequireAdminPin => PermissionAccessMode.RequireAdminPin,
            PermissionOverrideMode.Deny => PermissionAccessMode.Deny,
            _ => RolePermissionPolicy.For(role).Can(permission)
                ? PermissionAccessMode.Allow
                : permission is Permission.OverrideSalePrice or Permission.ManageSupplierPayments
                    ? PermissionAccessMode.RequireAdminPin
                    : PermissionAccessMode.Deny
        };
    }
}

public enum OperatorRole { Cashier, Supervisor, Administrator }

public static class RolePermissionPolicy
{
    public static PermissionSet For(OperatorRole role) => role switch
    {
        OperatorRole.Cashier => new PermissionSet(Permission.ConfirmSale, Permission.CreateCreditSale, Permission.CloseCashSession, Permission.ViewExchangeRate),
        OperatorRole.Supervisor => new PermissionSet(
            Permission.ConfirmSale, Permission.CreateCreditSale, Permission.ApplyDiscount, Permission.ChangePrice, Permission.AdjustStock,
            Permission.ReturnSale, Permission.ManageExchangeRate, Permission.CloseCashSession,
            Permission.ViewSensitiveReports, Permission.ManageCatalog, Permission.ManageCustomers,
            Permission.ManageReceivables, Permission.ViewReports, Permission.ViewExchangeRate, Permission.ViewInventory,
            Permission.ViewWarehouses, Permission.ManageWarehouses, Permission.TransferInventory, Permission.SelectPrice2,
            Permission.ViewSuppliers, Permission.ManageSuppliers, Permission.ViewPurchases, Permission.ManagePurchases,
            Permission.ViewFinancialReports),
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
