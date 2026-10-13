using System.Text.Json;
using Pos.Modules.Abstractions;
using Pos.Modules.ThermalReceipts;
using Pos.Shared.Contracts;

namespace Pos.Modules.Tests;

public sealed class ThermalReceiptModuleContractTests
{
    [Fact]
    public void Manifest_declares_versioned_server_and_desktop_entrypoints_and_permissions()
    {
        var root = FindRoot();
        var manifestPath = Path.Combine(root, "src", "Modules", "Pos.Modules.ThermalReceipts", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(manifestPath));

        Assert.Equal("thermal-receipts", manifest.Id);
        Assert.Equal(new Version(1, 0, 0), manifest.Version);
        Assert.Equal(new Version(1, 3, 0), manifest.DesktopApi!.Min);
        Assert.Contains("ThermalReceipts.Print", manifest.Permissions);
        Assert.Contains("ThermalReceipts.Configure", manifest.Permissions);
        Assert.Contains("thermal-receipts:0001", manifest.Migrations);
    }

    [Fact]
    public void Migration_creates_only_the_modules_shared_template_store()
    {
        var root = FindRoot();
        var migrationPath = Path.Combine(root, "src", "Modules", "Pos.Modules.ThermalReceipts", "migrations", "thermal-receipts-0001.json");
        using var json = JsonDocument.Parse(File.ReadAllText(migrationPath));

        var parsed = ModuleMigrationReader.Parse(File.ReadAllText(migrationPath));
        Assert.Single(parsed.Operations);
        Assert.Equal(1, json.RootElement.GetProperty("formatVersion").GetInt32());
        var operations = json.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Contains(operations, operation => operation.GetProperty("type").GetString() == "createTable" &&
            operation.GetProperty("table").GetString() == "receipt_templates");
    }

    [Fact]
    public void Default_template_is_valid_and_contains_basic_commercial_receipt_fields()
    {
        var template = ThermalReceiptTemplatePolicy.Default;

        Assert.Null(ThermalReceiptTemplatePolicy.Validate(template));
        Assert.Equal(80, template.PaperWidthMm);
        Assert.Contains("items", template.Sections);
        Assert.Contains("total", template.Sections);
        Assert.Equal("Center", template.Alignment);
    }

    [Theory]
    [InlineData(57)]
    [InlineData(76)]
    [InlineData(100)]
    public void Unsupported_paper_widths_are_rejected(int width)
    {
        var template = ThermalReceiptTemplatePolicy.Default with { PaperWidthMm = width };

        Assert.Contains("58 u 80", ThermalReceiptTemplatePolicy.Validate(template));
    }

    [Fact]
    public void Template_rejects_unknown_or_duplicate_fields_and_requires_amount_columns()
    {
        var unknown = ThermalReceiptTemplatePolicy.Default with { ItemColumns = ["description", "quantity", "lineTotal", "sql"] };
        var duplicate = ThermalReceiptTemplatePolicy.Default with { Sections = ["items", "items", "total"] };
        var missingTotal = ThermalReceiptTemplatePolicy.Default with { ItemColumns = ["description", "quantity"] };

        Assert.Contains("columnas", ThermalReceiptTemplatePolicy.Validate(unknown), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repetidas", ThermalReceiptTemplatePolicy.Validate(duplicate), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("importe", ThermalReceiptTemplatePolicy.Validate(missingTotal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_rejects_unbounded_or_control_character_text()
    {
        var longLine = ThermalReceiptTemplatePolicy.Default with { HeaderLines = [new string('A', 61)] };
        var control = ThermalReceiptTemplatePolicy.Default with
        {
            Labels = new Dictionary<string, string>(ThermalReceiptTemplatePolicy.Default.Labels) { ["total"] = "Total\nmalicioso" }
        };

        Assert.NotNull(ThermalReceiptTemplatePolicy.Validate(longLine));
        Assert.NotNull(ThermalReceiptTemplatePolicy.Validate(control));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
