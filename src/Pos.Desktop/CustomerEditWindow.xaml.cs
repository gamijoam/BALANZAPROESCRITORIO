using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CustomerEditWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly CustomerInfo _customer;

    public CustomerEditWindow(PosViewModel viewModel, CustomerInfo customer)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _customer = customer;
        CustomerNameLabel.Text = customer.Name;
        NameBox.Text = customer.Name;
        DocumentBox.Text = customer.DocumentNumber ?? string.Empty;
        PhoneBox.Text = customer.Phone ?? string.Empty;
        LimitBox.Text = PosNumberFormat.FormatAmount(customer.CreditLimit);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length is < 2 or > 200)
        {
            StatusLabel.Text = "El nombre debe tener entre 2 y 200 caracteres.";
            NameBox.Focus();
            return;
        }
        if (!PosNumberFormat.TryParseAmount(LimitBox.Text, out var creditLimit) || creditLimit < 0m || decimal.Round(creditLimit, 2) != creditLimit)
        {
            StatusLabel.Text = "El límite de crédito debe ser cero o mayor y tener máximo dos decimales.";
            LimitBox.Focus();
            return;
        }

        try
        {
            await _viewModel.UpdateCustomerAsync(_customer.Id, name, DocumentBox.Text, PhoneBox.Text, creditLimit);
            DialogResult = true;
        }
        catch (PosApiException ex) when (ex.Code == "credit_limit_below_current_balance")
        {
            StatusLabel.Text = "El límite no puede ser menor que la deuda pendiente del cliente.";
        }
        catch (PosApiException ex) when (ex.Code == "customer_document_already_exists")
        {
            StatusLabel.Text = "Ese documento ya está registrado en otro cliente.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = PosUserMessage.ForException(ex);
        }
    }
}
