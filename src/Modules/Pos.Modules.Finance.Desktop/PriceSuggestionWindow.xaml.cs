using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Modules.Finance.Desktop;

public partial class PriceSuggestionWindow : Window
{
    private const string WeightedAverageCostBasis = "WeightedAverage";
    private const string LastReceivedCostBasis = "LastReceivedCost";
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
            var costBasis = result?.CostBasis == LastReceivedCostBasis ? LastReceivedCostBasis : WeightedAverageCostBasis;
            CostBasisSummary.Text = costBasis == LastReceivedCostBasis
                ? "Base seleccionada: último costo recibido. Antes de la primera recepción se usa el costo inicial del producto; no incluye flete ni impuestos."
                : "Base seleccionada: promedio ponderado registrado en el inventario. Antes de la primera recepción se usa el costo inicial del producto.";
            var details = new List<string>();
            if (result?.HasReceivedItems != true) details.Add("La factura todavía no tiene artículos recibidos; las propuestas pueden usar el costo inicial del producto si no hay recepciones anteriores.");
            if (result?.MissingMarginTiers.Count > 0)
                details.Add("Sin margen configurado: " + string.Join(", ", result.MissingMarginTiers.Select(FinanceDisplayText.PriceTier)) + ".");
            if (result?.UnavailableProducts.Count > 0)
                details.Add("Artículos sin propuesta: " + string.Join("; ", result.UnavailableProducts.Select(item => $"{item.Sku} ({item.Reason})")) + ".");
            if (result?.UnavailableProducts.Any(item => item.Reason.Contains("costo promedio", StringComparison.OrdinalIgnoreCase) ||
                    item.Reason.Contains("costo inicial", StringComparison.OrdinalIgnoreCase)) == true)
                details.Add("Registra el costo inicial al crear el artículo o asígnalo desde Inventario.");
            if (result?.UnavailableProducts.Any(item => item.Reason.Contains("costo de inventario inicializado", StringComparison.OrdinalIgnoreCase)) == true)
                details.Add("Para confirmar una sugerencia de último costo, el artículo también necesita un costo de inventario inicializado para la validación de seguridad.");
            details.Add(_suggestions.Count == 0
                ? "No se cambió ningún precio. Revisa los requisitos indicados arriba y vuelve a abrir las sugerencias."
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
        ApplySuggestionButton.IsEnabled = SuggestionsGrid.SelectedItem is PriceSuggestionClientInfo { ProductCostSnapshot: not null };

    private async void ApplySuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestionsGrid.SelectedItem is not PriceSuggestionClientInfo suggestion) return;
        if (!suggestion.ProductCostSnapshot.HasValue)
        {
            SuggestionStatus.Text = "No se puede confirmar de forma segura porque falta el costo de inventario del artículo.";
            SuggestionStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        var suggestedPrice = decimal.Round(suggestion.SuggestedPrice, 2, MidpointRounding.AwayFromZero);
        var confirm = MessageBox.Show(this,
            $"¿Confirmas cambiar {suggestion.PriceTierLabel} de {suggestion.Name} a {FormatMoney(suggestion.Currency, suggestedPrice)}?\n\nBase: {suggestion.CostBasisLabel}\nOrigen del costo: {suggestion.CostSourceLabel}\nCosto utilizado: {FormatMoney(suggestion.Currency, suggestion.UnitCost)}\nMargen bruto: {suggestion.MarginPercent:N2} %\nFactura revisada: {suggestion.DocumentNumber}",
            "Confirmar cambio de precio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            ApplySuggestionButton.IsEnabled = false;
            var api = _context.ReportApiClient ?? throw new InvalidOperationException("El cliente del servicio no está disponible.");
            var reason = $"Sugerencia financiera ({suggestion.CostBasisLabel}) por margen {suggestion.MarginPercent.ToString("0.##", CultureInfo.InvariantCulture)} %; costo {suggestion.Currency} {suggestion.UnitCost.ToString("0.####", CultureInfo.InvariantCulture)}; factura {suggestion.DocumentNumber}.";
            await api.ApplySuggestedPriceAsync(suggestion.ProductId, suggestion.PriceTier, suggestedPrice,
                suggestion.CurrentPrice, suggestion.ProductCostSnapshot.Value, reason);
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

    public sealed record PriceSuggestionResultClientInfo(Guid InvoiceId, string DocumentNumber, string? CostBasis,
        IReadOnlyList<PriceSuggestionClientInfo> Suggestions, IReadOnlyList<string> MissingMarginTiers,
        IReadOnlyList<PriceSuggestionUnavailableClientInfo> UnavailableProducts, bool HasReceivedItems);
    public sealed record PriceSuggestionClientInfo(Guid ProductId, string Sku, string Name, string Currency, decimal UnitCost,
        string PriceTier, decimal MarginPercent, decimal? CurrentPrice, decimal SuggestedPrice, Guid InvoiceId, string DocumentNumber,
        decimal? ProductCostSnapshot, string? CostBasis, string? CostSourceKind, string? CostSourceDocumentNumber, DateTime? CostReceivedAtUtc)
    {
        public string PriceTierLabel => FinanceDisplayText.PriceTier(PriceTier);
        public string CostBasisLabel => CostBasis == LastReceivedCostBasis ? "Último costo recibido" : "Promedio ponderado";
        public string CostLabel => FormatMoney(Currency, UnitCost);
        public string CostSourceLabel => CostSourceKind switch
        {
            "InitialCost" => "Costo inicial",
            "Receipt" => $"{CostSourceDocumentNumber ?? "Recepción"} · {CostReceivedAtUtc?.ToLocalTime():dd/MM/yyyy HH:mm}",
            _ => "Promedio ponderado del inventario"
        };
        public string MarginLabel => $"{MarginPercent:N2} %";
        public string CurrentPriceLabel => CurrentPrice.HasValue ? FormatMoney(Currency, CurrentPrice.Value) : "Sin configurar";
        public string SuggestedPriceLabel => FormatMoney(Currency, decimal.Round(SuggestedPrice, 2, MidpointRounding.AwayFromZero));
    }
    public sealed record PriceSuggestionUnavailableClientInfo(Guid ProductId, string Sku, string Name, string Reason);

    private static string FormatMoney(string currency, decimal value)
        => $"{currency} {value.ToString("N2", CultureInfo.GetCultureInfo("es-VE"))}";
}
