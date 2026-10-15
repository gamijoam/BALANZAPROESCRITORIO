using Pos.Domain.Identity;

namespace Pos.Domain.Tests;

public sealed class OperatorPermissionPolicyTests
{
    [Fact]
    public void Inherit_uses_the_role_default()
    {
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ConfirmSale, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ManageUsers, PermissionOverrideMode.Inherit));
    }

    [Fact]
    public void Cashier_can_make_credit_sales_without_managing_receivable_accounts()
    {
        var cashier = RolePermissionPolicy.For(OperatorRole.Cashier);

        Assert.True(cashier.Can(Permission.CreateCreditSale));
        Assert.False(cashier.Can(Permission.ManageReceivables));
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.CreateCreditSale, PermissionOverrideMode.RequireAdminPin));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.CreateCreditSale, PermissionOverrideMode.Deny));
    }

    [Fact]
    public void Sale_line_price_override_requires_an_administrator_for_non_admin_roles()
    {
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.OverrideSalePrice, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.OverrideSalePrice, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Administrator, Permission.OverrideSalePrice, PermissionOverrideMode.Deny));
    }

    [Fact]
    public void Price2_is_supervisor_only_by_default_and_can_be_delegated_per_operator()
    {
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.SelectPrice2, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.SelectPrice2, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.SelectPrice2, PermissionOverrideMode.Allow));
    }

    [Fact]
    public void Initial_inventory_cost_requires_administrator_approval_for_non_admin_roles()
    {
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.InitializeInventoryCost, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.InitializeInventoryCost, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Administrator, Permission.InitializeInventoryCost, PermissionOverrideMode.Deny));
    }

    [Fact]
    public void Supplier_payments_require_administrator_approval_for_non_admin_roles_by_default()
    {
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ManageSupplierPayments, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.ManageSupplierPayments, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Administrator, Permission.ManageSupplierPayments, PermissionOverrideMode.Deny));
    }

    [Fact]
    public void Finance_view_and_purchase_management_permissions_default_to_supervisor()
    {
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.ViewSuppliers, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.ManagePurchases, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ManagePurchases, PermissionOverrideMode.Inherit));
    }

    [Fact]
    public void Per_operator_override_can_allow_deny_or_require_admin_pin()
    {
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ViewReports, PermissionOverrideMode.Allow));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ConfirmSale, PermissionOverrideMode.Deny));
        Assert.Equal(PermissionAccessMode.RequireAdminPin, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ManageCatalog, PermissionOverrideMode.RequireAdminPin));
    }

    [Fact]
    public void Administrator_cannot_become_unable_to_manage_operator_permissions()
    {
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Administrator, Permission.ManageUsers, PermissionOverrideMode.Deny));
    }

    [Fact]
    public void Only_administrator_can_manage_official_modules_by_default()
    {
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ManageModules, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.ManageModules, PermissionOverrideMode.Inherit));
        Assert.Equal(PermissionAccessMode.Allow, OperatorPermissionPolicy.Resolve(
            OperatorRole.Administrator, Permission.ManageModules, PermissionOverrideMode.Deny));
    }

    [Theory]
    [InlineData(PermissionOverrideMode.Allow)]
    [InlineData(PermissionOverrideMode.RequireAdminPin)]
    public void Module_management_cannot_be_delegated_to_non_administrators(PermissionOverrideMode overrideMode)
    {
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Cashier, Permission.ManageModules, overrideMode));
        Assert.Equal(PermissionAccessMode.Deny, OperatorPermissionPolicy.Resolve(
            OperatorRole.Supervisor, Permission.ManageModules, overrideMode));
    }
}
