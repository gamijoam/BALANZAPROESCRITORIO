using System.Windows;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Desktop.Modules.Abstractions;

/// <summary>Entry point for a trusted WPF module loaded by the desktop host.</summary>
public interface IPosDesktopModule
{
    string ModuleId { get; }
    void Configure(PosDesktopModuleRegistry registry);
}

public sealed record PosDesktopModuleContext(IPosModuleApiClient ApiClient, Guid TerminalId,
    IReadOnlySet<string> GrantedPermissions, string ModuleId,
    Func<string, CancellationToken, Task<IReadOnlyList<ProductInfo>>> SearchProductsAsync)
{
    public IPosApiClient? ReportApiClient { get; init; }
    public Func<IReadOnlyList<PosDesktopReportSourceInstance>>? GetReportSources { get; init; }
}

public sealed record PosDesktopMenuContribution(string Id, string Label, string RequiredPermission,
    Func<PosDesktopModuleContext, Window> CreateWindow);

public enum PosDesktopReportValueType { Text, Decimal, Integer, DateTime, Boolean }

public sealed record PosDesktopReportColumn(string Id, string Header, PosDesktopReportValueType ValueType,
    bool VisibleByDefault = true);

public sealed record PosDesktopReportQuery(DateTime? FromUtc, DateTime? ToUtc, string? Search,
    IReadOnlyDictionary<string, string> Filters);

public sealed record PosDesktopReportResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    public static PosDesktopReportResult Empty { get; } = new([]);
}

public sealed record PosDesktopReportSource(string Id, string Title, string RequiredPermission,
    IReadOnlyList<PosDesktopReportColumn> Columns,
    Func<PosDesktopModuleContext, PosDesktopReportQuery, CancellationToken, Task<PosDesktopReportResult>> LoadAsync,
    bool SupportsDateRange = false);

public sealed record PosDesktopReportSourceInstance(PosDesktopModuleContext Context, PosDesktopReportSource Source);

public sealed record PosDesktopReportCenterContribution(string Id, string Label, string RequiredPermission,
    Func<PosDesktopModuleContext, Window> CreateWindow);

public sealed record PosDesktopReceiptPrintResult(bool Accepted, string? Message = null);

/// <summary>Controlled hook used only after a sale has committed or when the operator explicitly opens a saved receipt.</summary>
public interface IPosReceiptExtension
{
    Task<PosDesktopReceiptPrintResult> PrintAsync(PosDesktopModuleContext context, SaleReceipt receipt,
        CancellationToken cancellationToken = default);
    Window CreatePreviewWindow(PosDesktopModuleContext context, SaleReceipt receipt);
}

public sealed record PosDesktopReceiptContribution(string RequiredPermission, IPosReceiptExtension Extension);

public static class ReportColumnProjection
{
    public static IReadOnlyList<PosDesktopReportColumn> Apply(IReadOnlyList<PosDesktopReportColumn> definitions,
        IReadOnlyList<ReportColumnLayout> layout)
    {
        var layoutById = layout.GroupBy(item => item.ColumnId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.DisplayOrder).First(), StringComparer.Ordinal);
        return definitions
            .Select((column, index) => (column, index, configured: layoutById.GetValueOrDefault(column.Id)))
            .Where(item => item.configured is null ? item.column.VisibleByDefault : item.configured.Visible)
            .OrderBy(item => item.configured?.DisplayOrder ?? item.index)
            .Select(item => item.column)
            .ToArray();
    }
}

/// <summary>Controlled extension point for adding a filter to the host inventory screen.</summary>
public interface IPosInventoryScreenExtension
{
    string RequiredPermission { get; }
    FrameworkElement CreateFilterControl(PosDesktopModuleContext context, Func<Task> refreshInventory);
    Task<IReadOnlyList<InventoryItemInfo>> ApplyFilterAsync(PosDesktopModuleContext context,
        IReadOnlyList<InventoryItemInfo> products, CancellationToken cancellationToken = default);
}

/// <summary>Optional read-only source of additional product inventory history, such as warehouse transfers.</summary>
public interface IPosInventoryHistoryExtension
{
    string RequiredPermission { get; }
    Task<ModuleInventoryMovementPageInfo> GetMovementsAsync(PosDesktopModuleContext context, Guid productId,
        int offset, int limit, CancellationToken cancellationToken = default);
}

public sealed class PosDesktopModuleRegistry
{
    private readonly string _moduleId;
    private readonly HashSet<string> _declaredPermissions;
    private readonly List<PosDesktopMenuContribution> _menus = [];
    private readonly List<IPosInventoryScreenExtension> _inventoryExtensions = [];
    private readonly List<IPosInventoryHistoryExtension> _inventoryHistoryExtensions = [];
    private readonly List<PosDesktopReportSource> _reportSources = [];
    private PosDesktopReceiptContribution? _receiptExtension;

    public PosDesktopModuleRegistry(string moduleId, IEnumerable<string> declaredPermissions)
    {
        _moduleId = moduleId;
        _declaredPermissions = declaredPermissions.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<PosDesktopMenuContribution> Menus => _menus;
    public IReadOnlyList<IPosInventoryScreenExtension> InventoryExtensions => _inventoryExtensions;
    public IReadOnlyList<IPosInventoryHistoryExtension> InventoryHistoryExtensions => _inventoryHistoryExtensions;
    public IReadOnlyList<PosDesktopReportSource> ReportSources => _reportSources;
    public PosDesktopReportCenterContribution? ReportCenter { get; private set; }
    public PosDesktopReceiptContribution? ReceiptExtension => _receiptExtension;

    public void RegisterReceiptExtension(string requiredPermission, IPosReceiptExtension extension)
    {
        if (_receiptExtension is not null)
            throw new InvalidOperationException("Solo se puede registrar una extensión de comprobantes.");
        ValidatePermission(requiredPermission);
        ArgumentNullException.ThrowIfNull(extension);
        _receiptExtension = new PosDesktopReceiptContribution(requiredPermission, extension);
    }

    public void RegisterReportCenter(string id, string label, string requiredPermission,
        Func<PosDesktopModuleContext, Window> createWindow)
    {
        if (ReportCenter is not null)
            throw new InvalidOperationException("Solo se puede registrar un centro de reportes avanzado.");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(char.IsControl))
            throw new InvalidOperationException("El ID del centro de reportes no es válido.");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 80 || label.Any(char.IsControl))
            throw new InvalidOperationException("El nombre del centro de reportes no es válido.");
        ValidatePermission(requiredPermission);
        ArgumentNullException.ThrowIfNull(createWindow);
        ReportCenter = new PosDesktopReportCenterContribution(id, label.Trim(), requiredPermission, createWindow);
    }

    public void RegisterReportSource(PosDesktopReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 96 ||
            !System.Text.RegularExpressions.Regex.IsMatch(source.Id, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$"))
            throw new InvalidOperationException("El ID de la fuente de reportes no es válido.");
        if (string.IsNullOrWhiteSpace(source.Title) || source.Title.Length > 120)
            throw new InvalidOperationException("El nombre de la fuente de reportes no es válido.");
        ValidatePermission(source.RequiredPermission);
        ArgumentNullException.ThrowIfNull(source.LoadAsync);
        if (_reportSources.Any(item => string.Equals(item.Id, source.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"La fuente de reporte '{source.Id}' ya fue registrado por el módulo '{_moduleId}'.");
        if (source.Columns.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != source.Columns.Count)
            throw new InvalidOperationException($"La fuente de reporte '{source.Id}' tiene columnas duplicadas.");
        _reportSources.Add(source);
    }

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

    public void RegisterInventoryHistoryExtension(IPosInventoryHistoryExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ValidatePermission(extension.RequiredPermission);
        if (_inventoryHistoryExtensions.Count != 0)
            throw new InvalidOperationException("Un módulo solo puede registrar una extensión de historial de inventario.");
        _inventoryHistoryExtensions.Add(extension);
    }

    private void ValidatePermission(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission) || !_declaredPermissions.Contains(permission))
            throw new InvalidOperationException($"El permiso '{permission}' debe estar declarado en el manifiesto de '{_moduleId}'.");
    }
}
