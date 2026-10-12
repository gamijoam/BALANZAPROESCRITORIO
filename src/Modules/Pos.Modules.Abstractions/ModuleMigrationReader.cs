using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pos.Modules.Abstractions;

public sealed class ModuleMigrationException(string message) : Exception(message);

public sealed record ModuleMigrationDefinition(IReadOnlyList<ModuleMigrationOperation> Operations);
public abstract record ModuleMigrationOperation;
public sealed record ModuleMigrationColumn(string Name, string DataType, bool Nullable, bool PrimaryKey);
public sealed record CreateModuleTable(string Table, IReadOnlyList<ModuleMigrationColumn> Columns) : ModuleMigrationOperation;
public sealed record AddNullableModuleColumn(string Table, string Column, string DataType) : ModuleMigrationOperation;
public sealed record CreateModuleIndex(string Table, string Name, IReadOnlyList<string> Columns, bool Unique) : ModuleMigrationOperation;

/// <summary>Reads a restricted, additive migration format; it does not accept executable SQL.</summary>
public static partial class ModuleMigrationReader
{
    private const int MaxDocumentBytes = 1024 * 1024;
    private const int MaxOperations = 512;
    private const int MaxColumnsPerTable = 128;
    private static readonly HashSet<string> SupportedDataTypes = new(StringComparer.Ordinal)
        { "uuid", "text", "integer", "bigint", "boolean", "numeric(18,4)", "timestamptz", "date" };

    public static ModuleMigrationDefinition Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw Failure("La migración está vacía o supera 1 MiB.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            EnsureProperties(root, ["formatVersion", "operations"], "migración");
            if (root.GetProperty("formatVersion").GetInt32() != 1)
                throw Failure("La versión de formato de migración no es compatible.");
            var operationsElement = root.GetProperty("operations");
            if (operationsElement.ValueKind != JsonValueKind.Array || operationsElement.GetArrayLength() is 0 or > MaxOperations)
                throw Failure($"La migración debe incluir entre 1 y {MaxOperations} operaciones.");
            var operations = operationsElement.EnumerateArray().Select(ParseOperation).ToArray();
            return new ModuleMigrationDefinition(operations);
        }
        catch (ModuleMigrationException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Failure($"El documento de migración no es válido: {exception.Message}");
        }
    }

    private static ModuleMigrationOperation ParseOperation(JsonElement operation)
    {
        if (operation.ValueKind != JsonValueKind.Object || !operation.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw Failure("Cada operación debe declarar su tipo.");
        var type = typeElement.GetString();
        return type switch
        {
            "createTable" => ParseCreateTable(operation),
            "addNullableColumn" => ParseAddNullableColumn(operation),
            "createIndex" => ParseCreateIndex(operation),
            _ => throw Failure($"El tipo de operación '{type}' no está permitido; no se admite SQL arbitrario ni cambios destructivos.")
        };
    }

    private static CreateModuleTable ParseCreateTable(JsonElement operation)
    {
        EnsureProperties(operation, ["type", "table", "columns"], "createTable");
        var table = ReadIdentifier(operation.GetProperty("table"), "tabla");
        var columnsElement = operation.GetProperty("columns");
        if (columnsElement.ValueKind != JsonValueKind.Array || columnsElement.GetArrayLength() is 0 or > MaxColumnsPerTable)
            throw Failure($"La tabla '{table}' debe declarar entre 1 y {MaxColumnsPerTable} columnas.");
        var columns = columnsElement.EnumerateArray().Select(column =>
        {
            EnsureProperties(column, ["name", "dataType", "nullable", "primaryKey"], "columna");
            var name = ReadIdentifier(column.GetProperty("name"), "columna");
            var dataType = ReadDataType(column.GetProperty("dataType"));
            var nullable = ReadBoolean(column.GetProperty("nullable"), "nullable");
            var primaryKey = ReadBoolean(column.GetProperty("primaryKey"), "primaryKey");
            if (primaryKey && nullable) throw Failure($"La columna clave '{name}' no puede ser nullable.");
            return new ModuleMigrationColumn(name, dataType, nullable, primaryKey);
        }).ToArray();
        if (columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw Failure($"La tabla '{table}' repite nombres de columna.");
        if (columns.Count(column => column.PrimaryKey) > 1)
            throw Failure($"La tabla '{table}' solo puede declarar una columna como clave primaria.");
        return new CreateModuleTable(table, columns);
    }

    private static AddNullableModuleColumn ParseAddNullableColumn(JsonElement operation)
    {
        EnsureProperties(operation, ["type", "table", "column", "dataType"], "addNullableColumn");
        return new AddNullableModuleColumn(
            ReadIdentifier(operation.GetProperty("table"), "tabla"),
            ReadIdentifier(operation.GetProperty("column"), "columna"),
            ReadDataType(operation.GetProperty("dataType")));
    }

    private static CreateModuleIndex ParseCreateIndex(JsonElement operation)
    {
        EnsureProperties(operation, ["type", "table", "name", "columns", "unique"], "createIndex");
        var table = ReadIdentifier(operation.GetProperty("table"), "tabla");
        var name = ReadIdentifier(operation.GetProperty("name"), "índice");
        var columnsElement = operation.GetProperty("columns");
        if (columnsElement.ValueKind != JsonValueKind.Array || columnsElement.GetArrayLength() is 0 or > MaxColumnsPerTable)
            throw Failure($"El índice '{name}' debe contener al menos una columna.");
        var columns = columnsElement.EnumerateArray().Select(column => ReadIdentifier(column, "columna de índice")).ToArray();
        if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw Failure($"El índice '{name}' repite columnas.");
        return new CreateModuleIndex(table, name, columns, ReadBoolean(operation.GetProperty("unique"), "unique"));
    }

    private static string ReadIdentifier(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value || !IdentifierPattern().IsMatch(value))
            throw Failure($"El nombre de {description} debe usar minúsculas, números y guion bajo, y tener hasta 63 caracteres.");
        return value;
    }

    private static string ReadDataType(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value || !SupportedDataTypes.Contains(value))
            throw Failure("El tipo de dato no está permitido.");
        return value;
    }

    private static bool ReadBoolean(JsonElement element, string property)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure($"La propiedad '{property}' debe ser booleana.");
        return element.GetBoolean();
    }

    private static void EnsureProperties(JsonElement element, string[] expected, string description)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Failure($"La operación {description} debe ser un objeto.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Failure($"La operación {description} repite '{property.Name}'.");
            if (!expected.Contains(property.Name, StringComparer.Ordinal)) throw Failure($"La operación {description} no admite '{property.Name}'.");
        }
        if (expected.Any(name => !seen.Contains(name))) throw Failure($"La operación {description} no contiene todas sus propiedades obligatorias.");
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    private static ModuleMigrationException Failure(string message) => new(message);
}
