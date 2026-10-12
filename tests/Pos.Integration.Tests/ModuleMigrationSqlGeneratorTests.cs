using Pos.Infrastructure.Modules;
using Pos.Modules.Abstractions;

namespace Pos.Integration.Tests;

public sealed class ModuleMigrationSqlGeneratorTests
{
    [Fact]
    public void Create_table_generates_only_quoted_module_schema_ddl()
    {
        var sql = ModuleMigrationSqlGenerator.Generate("m_warehouses_12345678",
            new CreateModuleTable("warehouse", [
                new ModuleMigrationColumn("id", "uuid", Nullable: false, PrimaryKey: true),
                new ModuleMigrationColumn("name", "text", Nullable: false, PrimaryKey: false)
            ]));

        Assert.Equal("CREATE TABLE \"m_warehouses_12345678\".\"warehouse\" (\"id\" uuid NOT NULL, \"name\" text NOT NULL, PRIMARY KEY (\"id\"));", sql);
    }

    [Fact]
    public void Add_column_is_always_nullable_and_index_uses_quoted_names()
    {
        Assert.Equal("ALTER TABLE \"m_warehouses_12345678\".\"warehouse\" ADD COLUMN \"extra\" numeric(18,4) NULL;",
            ModuleMigrationSqlGenerator.Generate("m_warehouses_12345678", new AddNullableModuleColumn("warehouse", "extra", "numeric(18,4)")));
        Assert.Equal("CREATE UNIQUE INDEX \"ix_warehouse_name\" ON \"m_warehouses_12345678\".\"warehouse\" (\"name\");",
            ModuleMigrationSqlGenerator.Generate("m_warehouses_12345678", new CreateModuleIndex("warehouse", "ix_warehouse_name", ["name"], true)));
    }
}
