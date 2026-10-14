using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Pos.Infrastructure.Persistence;

namespace Pos.Integration.Tests;

public sealed class PosDbContextMigrationSnapshotTests
{
    [Fact]
    public void CurrentModel_HasNoPendingChangesAgainstMigrationSnapshot()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=pos_model_snapshot_test;Username=unused;Password=unused")
            .Options;

        using var context = new PosDbContext(options);

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var modelInitializer = context.GetService<IModelRuntimeInitializer>();
        var validationLogger = context.GetService<IDiagnosticsLogger<DbLoggerCategory.Model.Validation>>();
        var snapshotModel = migrationsAssembly.ModelSnapshot is null
            ? null
            : modelInitializer.Initialize(migrationsAssembly.ModelSnapshot.Model, designTime: true, validationLogger);
        var currentDesignModel = modelInitializer.Initialize(
            context.GetService<IDesignTimeModel>().Model, designTime: true, validationLogger);
        var pendingOperations = snapshotModel is null
            ? "No migration model snapshot is registered."
            : string.Join(", ", differ.GetDifferences(snapshotModel.GetRelationalModel(), currentDesignModel.GetRelationalModel())
                .Select(operation => operation switch
                {
                    DropIndexOperation dropIndex => $"DropIndex {dropIndex.Table}.{dropIndex.Name}",
                    AddForeignKeyOperation addForeignKey => $"AddForeignKey {addForeignKey.Table}.{addForeignKey.Name} -> {addForeignKey.PrincipalTable}({string.Join(",", addForeignKey.PrincipalColumns ?? Array.Empty<string>())})",
                    _ => operation.GetType().Name
                }));

        Assert.False(context.Database.HasPendingModelChanges(),
            $"PosDbContext changed without a matching migration and model snapshot. Pending operations: {pendingOperations}");
        Assert.Contains("20261014180000_Hito30RemoveRedundantPurchaseReceiptEffectLineIndex", context.Database.GetMigrations());
    }
}
