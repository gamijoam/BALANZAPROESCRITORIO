using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Modules.Finance.Desktop;

public partial class PriceSuggestionWindow : Window
{
    private readonly PosDesktopModuleContext _context;
    private readonly Guid _invoiceId;
    private readonly string _documentLabel;
    private IReadOnlyList<PriceSuggestionClientInfo> _suggestions = [];

    public PriceSuggestionWindow(PosDesktopModuleContext context, Guid invoiceId, string documentLabel)
    {
        InitializeComponent();
        _context = context;
        _invoiceId = invoiceId;
        _documentLabel = documentLabel;
        InvoiceLabel.Text = documentLabel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadSuggestionsAsync();

    private async Task LoadSuggestionsAsync()
    {
        try
        {
            ApplySuggestionButton.IsEnabled = false;
            var result = await _context.ApiClient.GetModuleAsync<PriceSuggestionResultClientInfo>(
                "finance", $"purchases/{_invoiceId:D}/price-suggestions");
            _suggestions = result?.Suggestions ?? [];
            SuggestionsGrid.ItemsSource = _suggestions;
            SuggestionsEmptyLabel.Visibility = _suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var details = new List<string>();
            if (result?.HasReceivedItems != true) details.Add("La factura todavía no tiene artículos recibidos.");
            if (result?.MissingMarginTiers.Count > 0)
                details.Add("Sin margen configurado: " + string.Join(", ", result.MissingMarginTiers.Select(FinanceDisplayText.PriceTier)) + ".");
            if (result?.UnavailableProducts.Count > 0)
                details.Add("Artículos sin propuesta: " + string.Join("; ", result.UnavailableProducts.Select(item => $"{item.Sku} ({item.Reason})")) + ".");
            details.Add(_suggestions.Count == 0
                ? "No se cambió ningún precio. Configura márgenes y verifica que los artículos tengan costo promedio."
                : $"{_suggestions.Count} propuesta(s). Ninguna modifica el catálogo hasta que confirmes una.");
            SuggestionStatus.Text = string.Join(" ", details);
        }
        catch (Exception exception)
        {
            SuggestionStatus.Text = PosUserMessage.ForException(exception);
            SuggestionStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void SuggestionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplySuggestionButton.IsEnabled = SuggestionsGrid.SelectedItem is PriceSuggestionClientInfo;

    private async void ApplySuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestionsGrid.SelectedItem is not PriceSuggestionClientInfo suggestion) return;
        var confirm = MessageBox.Show(this,
            $"¿Confirmas cambiar {suggestion.PriceTierLabel} de {suggestion.Name} a {suggestion.Currency} {suggestion.SuggestedPrice:N4}?\n\nCosto promedio: {suggestion.Currency} {suggestion.UnitCost:N4}\nMargen bruto: {suggestion.MarginPercent:N2} %\nFactura de origen: {suggestion.DocumentNumber}",
            "Confirmar cambio de precio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            ApplySuggestionButton.IsEnabled = false;
            var api = _context.ReportApiClient ?? throw new InvalidOperationException("El cliente del servicio no está disponible.");
            var reason = $"Sugerencia financiera por margen {suggestion.MarginPercent.ToString("0.##", CultureInfo.InvariantCulture)} %; costo {suggestion.Currency} {suggestion.UnitCost.ToString("0.####", CultureInfo.InvariantCulture)}; factura {suggestion.DocumentNumber}.";
            await api.ApplySuggestedPriceAsync(suggestion.ProductId, suggestion.PriceTier, suggestion.SuggestedPrice,
                suggestion.CurrentPrice, suggestion.UnitCost, reason);
            SuggestionStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            SuggestionStatus.Text = $"Se actualizó {suggestion.PriceTierLabel} de {suggestion.Sku}; el historial de precios conserva el usuario, la fecha y la factura de origen.";
            await LoadSuggestionsAsync();
        }
        catch (Exception exception)
        {
            SuggestionStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            SuggestionStatus.Text = exception is PosApiException { Code: "stale_price_suggestion" }
                ? "El costo o el precio cambió mientras revisabas. Actualiza la ventana y confirma la propuesta nueva."
                : PosUserMessage.ForException(exception);
            ApplySuggestionButton.IsEnabled = SuggestionsGrid.SelectedItem is PriceSuggestionClientInfo;
        }
    }

    public sealed record PriceSuggestionResultClientInfo(Guid InvoiceId, string DocumentNumber,
        IReadOnlyList<PriceSuggestionClientInfo> Suggestions, IReadOnlyList<string> MissingMarginTiers,
        IReadOnlyList<PriceSuggestionUnavailableClientInfo> UnavailableProducts, bool HasReceivedItems);
    public sealed record PriceSuggestionClientInfo(Guid ProductId, string Sku, string Name, string Currency, decimal UnitCost,
        string PriceTier, decimal MarginPercent, decimal? CurrentPrice, decimal SuggestedPrice, Guid InvoiceId, string DocumentNumber)
    {
        public string PriceTierLabel => FinanceDisplayText.PriceTier(PriceTier);
        public string CostLabel => $"{Currency} {UnitCost:N4}";
        public string MarginLabel => $"{MarginPercent:N2} %";
        public string CurrentPriceLabel => CurrentPrice.HasValue ? $"{Currency} {CurrentPrice.Value:N4}" : "Sin configurar";
        public string SuggestedPriceLabel => $"{Currency} {SuggestedPrice:N4}";
    }
    public sealed record PriceSuggestionUnavailableClientInfo(Guid ProductId, string Sku, string Name, string Reason);
}
