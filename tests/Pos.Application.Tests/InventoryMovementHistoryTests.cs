namespace Pos.Application.Tests;

public sealed class InventoryMovementHistoryTests
{
    [Fact]
    public void Movement_endpoint_uses_bounded_stable_paging_and_returns_actor_and_terminal()
    {
        var root = FindRoot();
        var server = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs"));
        var endpoint = server[server.IndexOf("app.MapGet(\"/api/v1/inventory/{productId:guid}/movements\"", StringComparison.Ordinal)..];
        endpoint = endpoint[..endpoint.IndexOf("app.MapHito4()", StringComparison.Ordinal)];

        Assert.Contains("Skip(offset)", endpoint, StringComparison.Ordinal);
        Assert.Contains("Take(limit + 1)", endpoint, StringComparison.Ordinal);
        Assert.Contains("movement.Id descending", endpoint, StringComparison.Ordinal);
        Assert.Contains("movement.ActorId", endpoint, StringComparison.Ordinal);
        Assert.Contains("movement.TerminalId", endpoint, StringComparison.Ordinal);
        Assert.Contains("X-Has-More", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Movement_history_resolves_a_human_readable_operator_name_from_existing_users()
    {
        var root = FindRoot();
        var server = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs"));
        var endpoint = server[server.IndexOf("app.MapGet(\"/api/v1/inventory/{productId:guid}/movements\"", StringComparison.Ordinal)..];
        endpoint = endpoint[..endpoint.IndexOf("app.MapHito4()", StringComparison.Ordinal)];
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml"));

        Assert.Contains("ActorUsername", endpoint, StringComparison.Ordinal);
        Assert.Contains("actor.Username", endpoint, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding OperatorLabel}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_movement_history_window_displays_operator_terminal_and_page_controls()
    {
        var root = FindRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml.cs"));

        Assert.Contains("Header=\"Operador\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Terminal (ID)\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviousPageButton", xaml, StringComparison.Ordinal);
        Assert.Contains("NextPageButton", xaml, StringComparison.Ordinal);
        Assert.Contains("HasMore", code, StringComparison.Ordinal);
        Assert.Contains("GetStockMovementsAsync", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Warehouse_history_tab_is_hidden_without_an_authorized_module_extension()
    {
        var root = FindRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml"));
        var window = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InventoryWindow.xaml.cs"));
        var historyWindow = File.ReadAllText(Path.Combine(root, "src", "Pos.Desktop", "InventoryMovementsWindow.xaml.cs"));

        Assert.Contains("Header=\"Traslados\" Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GrantedPermissions.Contains(extension.RequiredPermission)", window, StringComparison.Ordinal);
        Assert.Contains("TransfersTab.Visibility = historyExtension is null || moduleContext is null ? Visibility.Collapsed : Visibility.Visible", historyWindow, StringComparison.Ordinal);
        Assert.Contains("GetMovementsAsync(_moduleContext, _product.Id", historyWindow, StringComparison.Ordinal);
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
