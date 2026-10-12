using Pos.Infrastructure.Modules;

namespace Pos.Integration.Tests;

public sealed class ModuleMaintenanceGateTests
{
    [Fact]
    public async Task Installation_waits_for_active_operations_and_blocks_new_ones_until_released()
    {
        var gate = new ModuleMaintenanceGate();
        var activeOperation = await gate.EnterOperationAsync();
        var installation = gate.EnterInstallationAsync();

        Assert.False(installation.IsCompleted);
        await activeOperation.DisposeAsync();
        var maintenance = await installation;
        var nextOperation = gate.EnterOperationAsync();
        Assert.False(nextOperation.IsCompleted);

        await maintenance.DisposeAsync();
        await using var operation = await nextOperation;
    }
}
