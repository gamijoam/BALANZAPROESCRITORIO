using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop.Modules.Abstractions;

/// <summary>Entry point for a trusted WPF module loaded by the desktop host.</summary>
public interface IPosDesktopModule
{
    string ModuleId { get; }
    void Configure(PosDesktopModuleRegistry registry);
}

public sealed record PosDesktopModuleContext(IPosModuleApiClient ApiClient, Guid TerminalId,
    IReadOnlySet<string> GrantedPermissions, string ModuleId,
    Func<string, CancellationToken, Task<IReadOnlyList<ProductInfo>>> SearchProductsAsync);

public sealed record PosDesktopMenuContribution(string Id, string Label, string RequiredPermission,
    Func<PosDesktopModuleContext, Window> CreateWindow);

/// <summary>Controlled extension point for adding a filter to the host inventory screen.</summary>
public interface IPosInventoryScreenExtension
{
    string RequiredPermission { get; }
    FrameworkElement CreateFilterControl(PosDesktopModuleContext context, Func<Task> refreshInventory);
    Task<IReadOnlyList<InventoryItemInfo>> ApplyFilterAsync(PosDesktopModuleContext context,
        IReadOnlyList<InventoryItemInfo> products, CancellationToken cancellationToken = default);
}

public sealed class PosDesktopModuleRegistry
{
    private readonly string _moduleId;
    private readonly HashSet<string> _declaredPermissions;
    private readonly List<PosDesktopMenuContribution> _menus = [];
    private readonly List<IPosInventoryScreenExtension> _inventoryExtensions = [];

    public PosDesktopModuleRegistry(string moduleId, IEnumerable<string> declaredPermissions)
    {
        _moduleId = moduleId;
        _declaredPermissions = declaredPermissions.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<PosDesktopMenuContribution> Menus => _menus;
    public IReadOnlyList<IPosInventoryScreenExtension> InventoryExtensions => _inventoryExtensions;

    public void RegisterMenu(string id, string label, string requiredPermission,
        Func<PosDesktopModuleContext, Window> createWindow)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(char.IsControl))
            throw new InvalidOperationException("El ID de la acción del módulo no es válido.");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 80 || label.Any(char.IsControl))
            throw new InvalidOperationException("El nombre de la acción del módulo no es válido.");
        ValidatePermission(requiredPermission);
        ArgumentNullException.ThrowIfNull(createWindow);
        if (_menus.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"El módulo '{_moduleId}' registró dos acciones con el ID '{id}'.");
        _menus.Add(new PosDesktopMenuContribution(id, label.Trim(), requiredPermission, createWindow));
    }

    public void RegisterInventoryExtension(IPosInventoryScreenExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ValidatePermission(extension.RequiredPermission);
        if (_inventoryExtensions.Count != 0)
            throw new InvalidOperationException("Un módulo solo puede registrar una extensión de inventario.");
        _inventoryExtensions.Add(extension);
    }

    private void ValidatePermission(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission) || !_declaredPermissions.Contains(permission))
            throw new InvalidOperationException($"El permiso '{permission}' debe estar declarado en el manifiesto de '{_moduleId}'.");
    }
}
