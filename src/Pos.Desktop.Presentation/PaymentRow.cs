using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pos.Desktop.Presentation;

/// <summary>Fila editable de pago; cambiar moneda convierte el importe con la tasa USD/VES actual.</summary>
public sealed class PaymentRow : INotifyPropertyChanged
{
    private readonly decimal? _usdToVes;
    private readonly bool _isFixed;
    private string _method;
    private string _amountText;
    private string _currency;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Method
    {
        get => _method;
        set => SetField(ref _method, value);
    }

    public string AmountText
    {
        get => _amountText;
        set
        {
            if (string.Equals(_amountText, value, StringComparison.Ordinal)) return;
            _amountText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EquivalentUsdText));
        }
    }

    public string EquivalentUsdText
    {
        get
        {
            if (!TryGetAmount(out var amount)) return "—";
            if (_currency == "USD") return PosNumberFormat.FormatAmount(amount);
            if (_currency != "VES" || _usdToVes is not > 0m) return "—";

            return PosNumberFormat.FormatAmount(decimal.Round(amount / _usdToVes.Value, 2, MidpointRounding.AwayFromZero));
        }
    }

    public string Currency
    {
        get => _currency;
        set
        {
            if (_isFixed || string.Equals(_currency, value, StringComparison.Ordinal)) return;
            if (!TryConvertAmount(_amountText, _currency, value, _usdToVes, out var convertedAmount))
            {
                OnPropertyChanged();
                return;
            }

            _currency = value;
            _amountText = convertedAmount;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AmountText));
            OnPropertyChanged(nameof(EquivalentUsdText));
        }
    }

    public PaymentRow(string method, string amountText, string currency, decimal? usdToVes = null, bool isFixed = false)
    {
        _method = method;
        _amountText = amountText;
        _currency = currency;
        _usdToVes = usdToVes;
        _isFixed = isFixed;
    }

    public bool TryGetAmount(out decimal amount) => PosNumberFormat.TryParseAmount(AmountText, out amount);

    private static bool TryConvertAmount(string amountText, string fromCurrency, string toCurrency, decimal? usdToVes, out string convertedAmount)
    {
        convertedAmount = amountText;
        if (fromCurrency is not ("USD" or "VES") || toCurrency is not ("USD" or "VES") || fromCurrency == toCurrency ||
            usdToVes is null or <= 0m || !PosNumberFormat.TryParseAmount(amountText, out var amount)) return false;

        try
        {
            var result = fromCurrency == "USD" ? amount * usdToVes.Value : amount / usdToVes.Value;
            convertedAmount = PosNumberFormat.FormatAmount(decimal.Round(result, 2, MidpointRounding.AwayFromZero));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
