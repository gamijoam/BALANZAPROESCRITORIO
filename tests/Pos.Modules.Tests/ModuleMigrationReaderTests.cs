using Pos.Modules.Abstractions;

namespace Pos.Modules.Tests;

public sealed class ModuleMigrationReaderTests
{
    [Fact]
    public void Reads_only_supported_additive_operations()
    {
        var migration = ModuleMigrationReader.Parse("""
            {
              "formatVersion": 1,
              "operations": [
                { "type": "createTable", "table": "warehouse", "columns": [
                  { "name": "id", "dataType": "uuid", "nullable": false, "primaryKey": true },
                  { "name": "name", "dataType": "text", "nullable": false, "primaryKey": false }
                ]},
                { "type": "addNullableColumn", "table": "warehouse", "column": "active", "dataType": "boolean" },
                { "type": "createIndex", "table": "warehouse", "name": "ix_warehouse_name", "columns": ["name"], "unique": true }
              ]
            }
            """);

        Assert.Equal(3, migration.Operations.Count);
        Assert.IsType<CreateModuleTable>(migration.Operations[0]);
        Assert.IsType<AddNullableModuleColumn>(migration.Operations[1]);
        Assert.IsType<CreateModuleIndex>(migration.Operations[2]);
    }

    [Theory]
    [InlineData("dropTable")]
    [InlineData("executeSql")]
    public void Rejects_destructive_or_arbitrary_operation_types(string operationType)
    {
        var json = $$"""
            {"formatVersion":1,"operations":[{"type":"{{operationType}}"}]}
            """;

        Assert.Throws<ModuleMigrationException>(() => ModuleMigrationReader.Parse(json));
    }

    [Fact]
    public void Rejects_non_nullable_column_addition_and_unsafe_identifiers()
    {
        var nonNullable = """
            {"formatVersion":1,"operations":[{"type":"addNullableColumn","table":"warehouse","column":"new_col","dataType":"text","nullable":false}]}
            """;
        var unsafeIdentifier = """
            {"formatVersion":1,"operations":[{"type":"addNullableColumn","table":"warehouse;DROP TABLE users","column":"new_col","dataType":"text"}]}
            """;

        Assert.Throws<ModuleMigrationException>(() => ModuleMigrationReader.Parse(nonNullable));
        Assert.Throws<ModuleMigrationException>(() => ModuleMigrationReader.Parse(unsafeIdentifier));
    }
}
