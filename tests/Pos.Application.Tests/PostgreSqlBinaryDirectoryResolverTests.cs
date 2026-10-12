using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PostgreSqlBinaryDirectoryResolverTests
{
    [Fact]
    public void Prefers_a_valid_configured_folder()
    {
        var result = PostgreSqlBinaryDirectoryResolver.Resolve(
            @"C:\PostgreSQL\18\bin", [],
            path => path == @"C:\PostgreSQL\18\bin");

        Assert.Equal(@"C:\PostgreSQL\18\bin", result);
    }

    [Fact]
    public void Finds_the_highest_installed_version_when_the_setting_is_missing_or_stale()
    {
        var result = PostgreSqlBinaryDirectoryResolver.Resolve(
            @"C:\missing\bin", [@"C:\Program Files\PostgreSQL\16\bin", @"C:\Program Files\PostgreSQL\18\bin"],
            path => path is @"C:\Program Files\PostgreSQL\16\bin" or @"C:\Program Files\PostgreSQL\18\bin");

        Assert.Equal(@"C:\Program Files\PostgreSQL\18\bin", result);
    }

    [Fact]
    public void Returns_null_when_postgresql_tools_are_not_installed()
    {
        var result = PostgreSqlBinaryDirectoryResolver.Resolve(null, [], _ => false);

        Assert.Null(result);
    }
}
