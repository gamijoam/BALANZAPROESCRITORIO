using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CustomersWindow : Window
{
    private readonly PosViewModel _viewModel;
    public CustomerInfo? SelectedCustomer { get; private set; }
    public CustomersWindow(PosViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; Loaded += async (_, _) => await LoadAsync(); }

    private async Task LoadAsync()
    {
        try { CustomersGrid.ItemsSource = await _viewModel.GetCustomersAsync(SearchBox.Text); StatusLabel.Text = ""; }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerInfo customer) { StatusLabel.Text = "Selecciona un cliente."; return; }
        SelectedCustomer = customer; DialogResult = true;
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(LimitBox.Text, out var limit) || limit < 0m)
        { StatusLabel.Text = "El límite de crédito no es válido."; return; }
        var currency = (CurrencyBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "VES";
        try
        {
            var customer = await _viewModel.CreateCustomerAsync(NameBox.Text, DocumentBox.Text, PhoneBox.Text, currency, limit);
            await LoadAsync();
            var created = (CustomersGrid.ItemsSource as IEnumerable<CustomerInfo>)?.FirstOrDefault(x => x.Id == customer.Id);
            if (created is not null) { CustomersGrid.SelectedItem = created; CustomersGrid.ScrollIntoView(created); }
            StatusLabel.Text = "Cliente creado. Selecciónalo para asignarlo a la venta.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void General_Click(object sender, RoutedEventArgs e) { SelectedCustomer = null; DialogResult = true; }
    private void Receivables_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerInfo customer) { StatusLabel.Text = "Selecciona un cliente."; return; }
        new ReceivablesWindow(_viewModel, customer) { Owner = this }.ShowDialog();
    }
}
