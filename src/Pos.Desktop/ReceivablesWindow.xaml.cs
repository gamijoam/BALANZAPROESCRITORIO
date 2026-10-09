using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ReceivablesWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly CustomerInfo _customer;
    public ReceivablesWindow(PosViewModel viewModel, CustomerInfo customer)
    {
        InitializeComponent(); _viewModel = viewModel; _customer = customer; CustomerTitle.Text = $"CUENTA · {customer.Name}";
        Loaded += async (_, _) => await LoadAsync();
    }
    private async Task LoadAsync()
    {
        try
        {
            var account = await _viewModel.GetReceivablesAsync(_customer.Id);
            BalanceLabel.Text = $"Saldo: {account.Currency} {account.Balance:N2} · Límite: {account.Currency} {account.CreditLimit:N2}";
            EntriesGrid.ItemsSource = account.Entries;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private async void Payment_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(AmountBox.Text, out var amount) || amount <= 0m)
        { StatusLabel.Text = "El abono debe ser mayor que cero."; return; }
        try
        {
            await _viewModel.RecordReceivablePaymentAsync(_customer.Id, amount, _customer.Currency, ReasonBox.Text.Trim());
            AmountBox.Clear(); ReasonBox.Clear(); StatusLabel.Text = "Abono registrado."; await LoadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
