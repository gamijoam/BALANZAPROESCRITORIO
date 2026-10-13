using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Contracts;

namespace Pos.Integration.Tests;

public sealed class TerminalClientVersionReportingTests
{
    [Fact]
    public void Saved_report_views_are_owned_by_operator_and_unique_by_report_and_name()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql("Host=localhost;Database=pos;Username=pos;Password=not-used")
            .Options;
        using var db = new PosDbContext(options);
        var entity = db.Model.FindEntityType(typeof(OperatorReportViewRecord));

        Assert.NotNull(entity);
        Assert.Equal("operator_report_views", entity!.GetTableName());
        Assert.Equal("jsonb", entity.FindProperty(nameof(OperatorReportViewRecord.ColumnsJson))!.GetColumnType());
        var index = Assert.Single(entity.GetIndexes(), item => item.IsUnique);
        Assert.Equal([nameof(OperatorReportViewRecord.OperatorId), nameof(OperatorReportViewRecord.ReportId), nameof(OperatorReportViewRecord.Name)],
            index.Properties.Select(property => property.Name));
        Assert.Contains(db.Database.GetMigrations(), name => name.Contains("OperatorReportViews", StringComparison.Ordinal));
    }

    [Fact]
    public void Client_version_records_have_one_current_version_per_terminal()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql("Host=localhost;Database=pos;Username=pos;Password=not-used")
            .Options;
        using var db = new PosDbContext(options);
        var entity = db.Model.FindEntityType(typeof(TerminalClientVersionRecord));

        Assert.NotNull(entity);
        Assert.Equal("terminal_client_versions", entity!.GetTableName());
        Assert.Equal(nameof(TerminalClientVersionRecord.TerminalId), entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal(32, entity.FindProperty(nameof(TerminalClientVersionRecord.ClientVersion))!.GetMaxLength());
        Assert.True(entity.FindProperty(nameof(TerminalClientVersionRecord.ClientVersion))!.IsNullable);
        Assert.Contains(db.Database.GetMigrations(), name => name.Contains("TerminalClientVersionReporting", StringComparison.Ordinal));
    }
}
