using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class AdminPinApprovalWindow : Window
{
    public AdministratorCredentials? Credentials { get; private set; }

    public AdminPinApprovalWindow(PermissionApprovalChallenge challenge)
    {
        InitializeComponent();
        ActionLabel.Text = $"El cajero necesita autorización para: {PermissionLabel(challenge.Permission)}.\n\nLa aprobación será válida solo para esta operación.";
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void Authorize_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrWhiteSpace(PinBox.Password))
        {
            ErrorLabel.Text = "Escribe el usuario administrador y su PIN.";
            return;
        }
        Credentials = new AdministratorCredentials(UsernameBox.Text.Trim(), PinBox.Password);
        PinBox.Clear();
        DialogResult = true;
    }

    private static string PermissionLabel(string permission) => permission switch
    {
        "ApplyDiscount" => "aplicar un descuento",
        "ChangePrice" => "cambiar un precio",
        "OverrideSalePrice" => "autorizar un precio especial para esta venta",
        "AdjustStock" => "ajustar inventario",
        "ViewInventory" => "consultar inventario y movimientos",
        "ReturnSale" => "registrar una devolución",
        "ManageExchangeRate" => "cambiar la tasa",
        "ManageCatalog" => "administrar productos",
        "ManageCustomers" => "administrar clientes",
        "ManageReceivables" => "gestionar créditos o abonos",
        "ViewReports" => "consultar o exportar reportes",
        "ViewSensitiveReports" => "consultar información sensible",
        "CloseCashSession" => "realizar una operación de caja",
        "ConfirmSale" => "facturar una venta",
        _ => permission
    };
}
