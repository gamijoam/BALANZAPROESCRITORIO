using Pos.Domain.Identity;

namespace Pos.Integration.Tests;

public sealed class SupervisorAuthorizationStoreTests
{
    [Fact]
    public void Approval_is_single_use_and_bound_to_user_terminal_permission_route_and_payload()
    {
        var store = new SupervisorAuthorizationStore();
        var operatorId = Guid.NewGuid();
        var administratorId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        const string route = "PATCH /api/v1/products/123/active";
        var fingerprint = new string('a', 64);
        var token = store.Issue(operatorId, administratorId, terminalId, Permission.ManageCatalog, route, fingerprint, operationId);

        Assert.False(store.TryConsume(token, operatorId, terminalId, Permission.ManageCatalog, route, new string('b', 64)));
        Assert.True(store.TryConsume(token, operatorId, terminalId, Permission.ManageCatalog, route, fingerprint));
        Assert.False(store.TryConsume(token, operatorId, terminalId, Permission.ManageCatalog, route, fingerprint));

        var validToken = store.Issue(operatorId, administratorId, terminalId, Permission.ManageCatalog, route, fingerprint, Guid.NewGuid());
        Assert.False(store.TryConsume(validToken, Guid.NewGuid(), terminalId, Permission.ManageCatalog, route, fingerprint));
        Assert.False(store.TryConsume(validToken, operatorId, Guid.NewGuid(), Permission.ManageCatalog, route, fingerprint));
        Assert.False(store.TryConsume(validToken, operatorId, terminalId, Permission.ReturnSale, route, fingerprint));
        Assert.False(store.TryConsume(validToken, operatorId, terminalId, Permission.ManageCatalog, "GET /api/v1/products", fingerprint));
        Assert.True(store.TryConsume(validToken, operatorId, terminalId, Permission.ManageCatalog, route, fingerprint));
        Assert.False(store.TryConsume(validToken, operatorId, terminalId, Permission.ManageCatalog, route, fingerprint));
    }
}
