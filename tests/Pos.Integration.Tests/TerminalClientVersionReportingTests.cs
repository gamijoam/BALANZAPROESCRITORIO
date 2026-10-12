using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;

namespace Pos.Integration.Tests;

public sealed class TerminalClientVersionReportingTests
{
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
