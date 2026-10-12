using Pos.Domain.Identity;
using Pos.Domain.Finance;
using Pos.Domain.Customers;

namespace Pos.Domain.Tests;

public class AuthorizationAndReceivablesTests
{
    [Fact]
    public void Permission_set_checks_explicit_permissions()
    {
        var permissions = new PermissionSet(Permission.ConfirmSale, Permission.ApplyDiscount);

        Assert.True(permissions.Can(Permission.ConfirmSale));
        Assert.False(permissions.Can(Permission.ChangePrice));
    }

    [Fact]
    public void Permission_set_rejects_unknown_permission_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PermissionSet((Permission)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PermissionSet(Permission.ConfirmSale, (Permission)999));
    }

    [Fact]
    public void Role_policy_grants_cashiers_sales_but_restricts_inventory_and_price_changes()
    {
        var cashier = RolePermissionPolicy.For(OperatorRole.Cashier);
        var supervisor = RolePermissionPolicy.For(OperatorRole.Supervisor);
        var administrator = RolePermissionPolicy.For(OperatorRole.Administrator);

        Assert.True(cashier.Can(Permission.ConfirmSale));
        Assert.True(cashier.Can(Permission.CloseCashSession));
        Assert.True(cashier.Can(Permission.ViewExchangeRate));
        Assert.False(cashier.Can(Permission.ManageExchangeRate));
        Assert.False(cashier.Can(Permission.AdjustStock));
        Assert.False(cashier.Can(Permission.ChangePrice));
        Assert.True(supervisor.Can(Permission.AdjustStock));
        Assert.False(supervisor.Can(Permission.ManageUsers));
        Assert.True(administrator.Can(Permission.ManageUsers));
    }

    [Fact]
    public void Return_permission_is_limited_to_supervisor_and_administrator()
    {
        Assert.False(RolePermissionPolicy.For(OperatorRole.Cashier).Can(Permission.ReturnSale));
        Assert.True(RolePermissionPolicy.For(OperatorRole.Supervisor).Can(Permission.ReturnSale));
        Assert.True(RolePermissionPolicy.For(OperatorRole.Administrator).Can(Permission.ReturnSale));
    }

    [Fact]
    public void Supervisor_approval_is_scoped_to_one_operation_and_consumed_once()
    {
        var operationId = Guid.NewGuid();
        var approval = SupervisorApproval.Issue(
            Permission.ApplyDiscount, Guid.NewGuid(), operationId,
            new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.False(approval.TryConsume(Permission.ApplyDiscount, Guid.NewGuid(), now));
        Assert.True(approval.TryConsume(Permission.ApplyDiscount, operationId, now));
        Assert.False(approval.TryConsume(Permission.ApplyDiscount, operationId, now));
    }

    [Fact]
    public void Supervisor_approval_expires_and_cannot_authorize_a_different_permission()
    {
        var operationId = Guid.NewGuid();
        var approval = SupervisorApproval.Issue(
            Permission.ApplyDiscount, Guid.NewGuid(), operationId,
            new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero));

        Assert.False(approval.TryConsume(Permission.ChangePrice, operationId,
            new DateTimeOffset(2026, 10, 8, 12, 30, 0, TimeSpan.Zero)));
        Assert.False(approval.TryConsume(Permission.ApplyDiscount, operationId,
            new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Supervisor_approval_can_only_be_consumed_once_under_concurrency()
    {
        var operationId = Guid.NewGuid();
        var approval = SupervisorApproval.Issue(
            Permission.ApplyDiscount, Guid.NewGuid(), operationId,
            new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero));
        var accepted = 0;

        Parallel.For(0, 10_000, _ =>
        {
            if (approval.TryConsume(Permission.ApplyDiscount, operationId,
                    new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Supervisor_approval_requires_actor_operation_and_utc_time()
    {
        var expires = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisorApproval.Issue((Permission)999, Guid.NewGuid(), Guid.NewGuid(), expires));
        Assert.Throws<ArgumentException>(() => SupervisorApproval.Issue(Permission.ApplyDiscount, Guid.Empty, Guid.NewGuid(), expires));
        Assert.Throws<ArgumentException>(() => SupervisorApproval.Issue(Permission.ApplyDiscount, Guid.NewGuid(), Guid.Empty, expires));
        Assert.Throws<ArgumentException>(() => SupervisorApproval.Issue(Permission.ApplyDiscount, Guid.NewGuid(), Guid.NewGuid(), expires.ToOffset(TimeSpan.FromHours(-4))));
        var approval = SupervisorApproval.Issue(Permission.ApplyDiscount, Guid.NewGuid(), Guid.NewGuid(), expires);
        Assert.Throws<ArgumentException>(() => approval.TryConsume(Permission.ApplyDiscount, approval.OperationId, expires.ToOffset(TimeSpan.FromHours(-4))));
    }

    [Fact]
    public void Receivable_account_enforces_credit_limit_and_prevents_overpayment()
    {
        var account = ReceivableAccount.Create(Guid.NewGuid(), new Money(100m, Currency.USD));
        account.Charge(new Money(80m, Currency.USD));

        Assert.Equal(new Money(80m, Currency.USD), account.Balance);
        Assert.Throws<DomainRuleException>(() => account.Charge(new Money(21m, Currency.USD)));
        Assert.Throws<DomainRuleException>(() => account.ApplyPayment(new Money(81m, Currency.USD)));
        Assert.Equal(new Money(80m, Currency.USD), account.Balance);
    }

    [Fact]
    public void Receivable_account_rejects_currency_mismatch_and_records_payment()
    {
        var account = ReceivableAccount.Create(Guid.NewGuid(), new Money(100m, Currency.USD));
        account.Charge(new Money(80m, Currency.USD));

        Assert.Throws<DomainRuleException>(() => account.ApplyPayment(new Money(5m, Currency.VES)));
        account.ApplyPayment(new Money(30m, Currency.USD));
        Assert.Equal(new Money(50m, Currency.USD), account.Balance);
    }

    [Fact]
    public void Receivable_account_accepts_exact_limit_and_payment_can_clear_the_balance()
    {
        var account = ReceivableAccount.Create(Guid.NewGuid(), new Money(100m, Currency.USD));
        account.Charge(new Money(100m, Currency.USD));
        account.ApplyPayment(new Money(100m, Currency.USD));

        Assert.Equal(new Money(0m, Currency.USD), account.Balance);
    }

    [Fact]
    public void Receivable_account_rejects_negative_limit_and_zero_value_operations()
    {
        Assert.Throws<ArgumentException>(() => ReceivableAccount.Create(Guid.Empty, new Money(0m, Currency.USD)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReceivableAccount.Create(Guid.NewGuid(), new Money(-1m, Currency.USD)));
        var account = ReceivableAccount.Create(Guid.NewGuid(), new Money(0m, Currency.USD));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.Charge(new Money(0m, Currency.USD)));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.ApplyPayment(new Money(0m, Currency.USD)));
    }
}
