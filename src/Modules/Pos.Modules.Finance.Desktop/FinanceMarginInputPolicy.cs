using System.Globalization;

namespace Pos.Modules.Finance.Desktop;

/// <summary>Parses margin percentages without treating the alternate decimal mark as a thousands separator.</summary>
public static class FinanceMarginInputPolicy
{
    public static bool TryParsePercent(string? text, out decimal? percent)
    {
        percent = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var valueText = text.Trim();
        var comma = valueText.IndexOf(',');
        var dot = valueText.IndexOf('.');
        if (comma >= 0 && dot >= 0 || comma >= 0 && valueText.LastIndexOf(',') != comma || dot >= 0 && valueText.LastIndexOf('.') != dot)
            return false;

        var separator = Math.Max(comma, dot);
        var integerLength = separator < 0 ? valueText.Length : separator;
        var fractionLength = separator < 0 ? 0 : valueText.Length - separator - 1;
        if (integerLength == 0 || fractionLength > 2 || separator >= 0 && fractionLength == 0)
            return false;
        if (!AllDigits(valueText.AsSpan(0, integerLength)) || separator >= 0 && !AllDigits(valueText.AsSpan(separator + 1)))
            return false;

        var normalized = comma >= 0 ? valueText.Replace(',', '.') : valueText;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0m || value >= 100m)
            return false;

        percent = value;
        return true;
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is < '0' or > '9') return false;
        return true;
    }
}
