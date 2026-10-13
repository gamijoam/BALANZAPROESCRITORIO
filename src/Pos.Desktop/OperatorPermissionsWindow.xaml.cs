using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class OperatorPermissionsWindow : Window
{
    public IReadOnlyList<PermissionModeOption> ModeOptions { get; } =
    [
        new("Heredar del rol", "Inherit"),
        new("Permitir", "Allow"),
        new("Pedir PIN admin", "RequireAdminPin"),
        new("Bloquear", "Deny")
    ];

    private readonly PosViewModel _viewModel;
    private readonly OperatorInfo _operator;
    private List<PermissionEditRow> _rows = [];

    public OperatorPermissionsWindow(PosViewModel viewModel, OperatorInfo operatorInfo)
    {
        InitializeComponent();
        DataContext = this;
        _viewModel = viewModel;
        _operator = operatorInfo;
        OperatorLabel.Text = $"{operatorInfo.Username} · Perfil {operatorInfo.RoleLabel}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var settings = await _viewModel.GetOperatorPermissionsAsync(_operator.Id);
            _rows = settings.Select(x => new PermissionEditRow(x.Permission, PermissionLabel(x.Permission), x.Mode, EffectiveLabel(x.Effective))).ToList();
            PermissionGrid.ItemsSource = _rows;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        PermissionGrid.CommitEdit();
        PermissionGrid.CommitEdit();
        try
        {
            await _viewModel.SetOperatorPermissionsAsync(_operator.Id,
                _rows.Select(x => new OperatorPermissionSetting(x.Permission, x.Mode, x.Effective)).ToArray());
            DialogResult = true;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string EffectiveLabel(string mode) => mode switch
    {
        "Allow" => "Permitido",
        "RequireAdminPin" => "Pide PIN admin",
        _ => "Bloqueado"
    };

    private static string PermissionLabel(string permission) => permission switch
    {
        "ConfirmSale" => "Facturar ventas",
        "CreateCreditSale" => "Vender a crédito",
        "ApplyDiscount" => "Aplicar descuentos",
        "ChangePrice" => "Cambiar precios",
        "OverrideSalePrice" => "Autorizar precio especial en una venta",
        "AdjustStock" => "Ajustar inventario",
        "ViewInventory" => "Consultar inventario y movimientos",
        "ReturnSale" => "Devolver ventas",
        "ManageUsers" => "Administrar operadores",
        "ManageExchangeRate" => "Cambiar tasa",
        "CloseCashSession" => "Abrir, cerrar y mover caja",
        "ViewSensitiveReports" => "Ver auditoría e informes sensibles",
        "ManageCatalog" => "Crear y modificar productos",
        "ManageCustomers" => "Administrar clientes",
        "ManageReceivables" => "Gestionar créditos y abonos",
        "ViewReports" => "Consultar y exportar reportes",
        "ViewExchangeRate" => "Consultar tasa",
        _ => permission
    };

    public sealed class PermissionEditRow(string permission, string label, string mode, string effective)
    {
        public string Permission { get; } = permission;
        public string Label { get; } = label;
        public string Mode { get; set; } = mode;
        public string Effective { get; } = effective;
    }

    public sealed record PermissionModeOption(string Label, string Value);
}
