using System.Windows;
using System.Windows.Input;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CustomersWindow : Window
{
    private readonly PosViewModel _viewModel;
    public CustomerInfo? SelectedCustomer { get; private set; }
    public CustomersWindow(PosViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    private async Task LoadAsync(bool focusFirstResult = false)
    {
        try
        {
            var customers = await _viewModel.GetCustomersAsync(SearchBox.Text);
            CustomersGrid.ItemsSource = customers;
            StatusLabel.Text = customers.Count == 0 ? "No se encontraron clientes." : string.Empty;
            if (!focusFirstResult) return;
            if (customers.Count == 0)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                return;
            }
            CustomersGrid.SelectedIndex = 0;
            CustomersGrid.ScrollIntoView(CustomersGrid.SelectedItem);
            CustomersGrid.Focus();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await LoadAsync(focusFirstResult: true);
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadAsync(focusFirstResult: true);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressedKey == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }
        if (pressedKey != Key.F2) return;
        e.Handled = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        await LoadAsync();
        SearchBox.Focus();
    }
    private void CustomersGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Select_Click(sender, new RoutedEventArgs());
            return;
        }
        if (e.Key is not (Key.Up or Key.Down) || CustomersGrid.Items.Count == 0) return;
        var selected = CustomersGrid.SelectedIndex;
        var next = e.Key == Key.Down
            ? Math.Min(CustomersGrid.Items.Count - 1, selected + 1)
            : Math.Max(0, selected < 0 ? 0 : selected - 1);
        CustomersGrid.SelectedIndex = next;
        CustomersGrid.ScrollIntoView(CustomersGrid.SelectedItem);
        e.Handled = true;
    }
    private void CustomersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var hasCustomer = CustomersGrid.SelectedItem is CustomerInfo;
        UseCustomerButton.IsEnabled = hasCustomer;
        ReceivablesButton.IsEnabled = hasCustomer;
        EditCustomerButton.IsEnabled = hasCustomer;
        DeleteCustomerButton.IsEnabled = hasCustomer;
    }
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerInfo customer) { StatusLabel.Text = "Selecciona un cliente."; return; }
        SelectedCustomer = customer; DialogResult = true;
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (NameBox.Text.Trim().Length < 2) { StatusLabel.Text = "Escribe el nombre del cliente (mínimo 2 caracteres)."; NameBox.Focus(); return; }
        var limit = 0m;
        if (!string.IsNullOrWhiteSpace(LimitBox.Text) &&
            (!PosNumberFormat.TryParseAmount(LimitBox.Text, out limit) || limit < 0m))
        { StatusLabel.Text = "El límite de crédito no es válido."; LimitBox.Focus(); LimitBox.SelectAll(); return; }
        try
        {
            var customer = await _viewModel.CreateCustomerAsync(NameBox.Text, DocumentBox.Text, PhoneBox.Text, "USD", limit);
            NameBox.Clear(); DocumentBox.Clear(); PhoneBox.Clear(); LimitBox.Clear();
            SearchBox.Clear();
            await LoadAsync();
            var created = (CustomersGrid.ItemsSource as IEnumerable<CustomerInfo>)?.FirstOrDefault(x => x.Id == customer.Id);
            if (created is not null) { CustomersGrid.SelectedItem = created; CustomersGrid.ScrollIntoView(created); }
            CustomersGrid.Focus();
            StatusLabel.Text = string.Empty;
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private async void EditCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerInfo customer) { StatusLabel.Text = "Selecciona un cliente."; return; }
        var dialog = new CustomerEditWindow(_viewModel, customer) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await LoadAsync();
        var updated = (CustomersGrid.ItemsSource as IEnumerable<CustomerInfo>)?.FirstOrDefault(x => x.Id == customer.Id);
        if (updated is not null) { CustomersGrid.SelectedItem = updated; CustomersGrid.ScrollIntoView(updated); }
        StatusLabel.Text = "Datos del cliente actualizados.";
        CustomersGrid.Focus();
    }
    private async void DeleteCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerInfo customer) { StatusLabel.Text = "Selecciona un cliente."; return; }
        var confirmation = MessageBox.Show(this,
            $"¿Deseas eliminar a «{customer.Name}» de la lista activa? Se conservará su historial y solo se podrá retirar si su saldo pendiente es cero.",
            "Eliminar cliente", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.DeactivateCustomerAsync(customer.Id);
            CustomersGrid.SelectedItem = null;
            await LoadAsync();
            StatusLabel.Text = string.Empty;
            CustomersGrid.Focus();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void General_Click(object sender, RoutedEventArgs e) { SelectedCustomer = null; DialogResult = true; }
    private async void Receivables_Click(object sender, RoutedEventArgs e)
    {
        if (CustomersGrid.SelectedItem is not CustomerInfo customer) { StatusLabel.Text = "Selecciona un cliente."; return; }
        new ReceivablesWindow(_viewModel, customer) { Owner = this }.ShowDialog();
        await LoadAsync();
        var refreshed = (CustomersGrid.ItemsSource as IEnumerable<CustomerInfo>)?.FirstOrDefault(x => x.Id == customer.Id);
        if (refreshed is not null) { CustomersGrid.SelectedItem = refreshed; CustomersGrid.ScrollIntoView(refreshed); }
        CustomersGrid.Focus();
    }
}
