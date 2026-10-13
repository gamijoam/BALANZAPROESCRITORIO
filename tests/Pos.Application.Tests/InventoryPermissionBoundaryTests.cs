namespace Pos.Application.Tests;

public sealed class InventoryPermissionBoundaryTests
{
    [Fact]
    public void Inventory_reads_require_view_permission_while_mutations_require_adjust_permission()
    {
        var root = FindRepositoryRoot();
        var server = File.ReadAllText(Path.Combine(root.FullName, "src", "Pos.Server", "Program.cs"));
        var readEndpoint = EndpointBody(server, "app.MapGet(\"/api/v1/inventory\"", "app.MapPost(\"/api/v1/inventory/{productId:guid}/adjustments\"");
        var adjustmentEndpoint = EndpointBody(server, "app.MapPost(\"/api/v1/inventory/{productId:guid}/adjustments\"", "app.MapGet(\"/api/v1/inventory/{productId:guid}/movements\"");
        var movementEndpoint = EndpointBody(server, "app.MapGet(\"/api/v1/inventory/{productId:guid}/movements\"", "app.MapHito4();");

        Assert.Contains("Permission.ViewInventory", readEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Permission.AdjustStock", readEndpoint, StringComparison.Ordinal);
        Assert.Contains("Permission.AdjustStock", adjustmentEndpoint, StringComparison.Ordinal);
        Assert.Contains("Permission.ViewInventory", movementEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Permission.AdjustStock", movementEndpoint, StringComparison.Ordinal);
    }

    private static string EndpointBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find endpoint marker: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find endpoint end marker: {endMarker}");
        return source[start..end];
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("Could not find the POS repository root.");
    }
}
