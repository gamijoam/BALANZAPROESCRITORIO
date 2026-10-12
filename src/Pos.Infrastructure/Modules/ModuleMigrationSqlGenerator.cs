using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pos.Modules.Abstractions;

namespace Pos.Infrastructure.Modules;

/// <summary>Compiles the signed migration DSL to additive PostgreSQL DDL.</summary>
public static partial class ModuleMigrationSqlGenerator
{
    private static readonly HashSet<string> SupportedDataTypes = new(StringComparer.Ordinal)
        { "uuid", "text", "integer", "bigint", "boolean", "numeric(18,4)", "timestamptz", "date" };

    public static string SchemaForModule(string moduleId)
    {
        if (!ModuleIdPattern().IsMatch(moduleId)) throw new ArgumentException("Invalid module id.", nameof(moduleId));
        var stem = moduleId.Replace('-', '_');
        if (stem.Length > 42) stem = stem[..42];
        var suffix = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(moduleId)))[..12].ToLowerInvariant();
        return $"m_{stem}_{suffix}";
    }

    public static string Generate(string moduleSchema, ModuleMigrationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireIdentifier(moduleSchema);
        return operation switch
        {
            CreateModuleTable create => GenerateCreateTable(moduleSchema, create),
            AddNullableModuleColumn add => GenerateAddNullableColumn(moduleSchema, add),
            CreateModuleIndex index => GenerateCreateIndex(moduleSchema, index),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), "Unsupported module migration operation.")
        };
    }

    private static string GenerateCreateTable(string schema, CreateModuleTable create)
    {
        RequireIdentifier(create.Table);
        if (create.Columns.Count == 0) throw new ArgumentException("A module table must contain columns.", nameof(create));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<string>();
        var primaryKeys = new List<string>();
        foreach (var column in create.Columns)
        {
            RequireIdentifier(column.Name);
            RequireDataType(column.DataType);
            if (!names.Add(column.Name)) throw new ArgumentException("Duplicate module column.", nameof(create));
            definitions.Add($"{Quote(column.Name)} {column.DataType}{(column.Nullable ? " NULL" : " NOT NULL")}");
            if (column.PrimaryKey)
            {
                if (column.Nullable || primaryKeys.Count > 0) throw new ArgumentException("Module table supports one non-null primary key column.", nameof(create));
                primaryKeys.Add(Quote(column.Name));
            }
        }
        if (primaryKeys.Count > 0) definitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKeys)})");
        return $"CREATE TABLE {Quote(schema)}.{Quote(create.Table)} ({string.Join(", ", definitions)});";
    }

    private static string GenerateAddNullableColumn(string schema, AddNullableModuleColumn add)
    {
        RequireIdentifier(add.Table);
        RequireIdentifier(add.Column);
        RequireDataType(add.DataType);
        return $"ALTER TABLE {Quote(schema)}.{Quote(add.Table)} ADD COLUMN {Quote(add.Column)} {add.DataType} NULL;";
    }

    private static string GenerateCreateIndex(string schema, CreateModuleIndex index)
    {
        RequireIdentifier(index.Table);
        RequireIdentifier(index.Name);
        if (index.Columns.Count == 0) throw new ArgumentException("A module index must contain columns.", nameof(index));
        foreach (var column in index.Columns) RequireIdentifier(column);
        return $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {Quote(index.Name)} ON {Quote(schema)}.{Quote(index.Table)} ({string.Join(", ", index.Columns.Select(Quote))});";
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";

    private static void RequireIdentifier(string identifier)
    {
        if (!IdentifierPattern().IsMatch(identifier)) throw new ArgumentException("Invalid identifier in module migration.", nameof(identifier));
    }

    private static void RequireDataType(string dataType)
    {
        if (!SupportedDataTypes.Contains(dataType)) throw new ArgumentException("Invalid data type in module migration.", nameof(dataType));
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleIdPattern();
}
