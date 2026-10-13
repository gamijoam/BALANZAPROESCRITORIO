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

    [Fact]
    public void Thermal_receipt_migration_generates_an_additive_template_table()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            root = root.Parent;
        Assert.NotNull(root);
        var migrationPath = Path.Combine(root!.FullName, "src", "Modules", "Pos.Modules.ThermalReceipts", "migrations", "thermal-receipts-0001.json");
        var migration = ModuleMigrationReader.Parse(File.ReadAllText(migrationPath));
        var createTable = Assert.IsType<CreateModuleTable>(Assert.Single(migration.Operations));

        var sql = ModuleMigrationSqlGenerator.Generate("m_thermal_receipts_123456789abc", createTable);
        Assert.StartsWith("CREATE TABLE \"m_thermal_receipts_123456789abc\".\"receipt_templates\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"template_json\" text NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"revision\" bigint NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
    }
}
