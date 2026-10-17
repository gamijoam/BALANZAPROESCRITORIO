using System.Globalization;
using System.Text;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductUnitCreateWindow : Window
{
    private readonly HashSet<string> _existingAbbreviations;
    public string UnitName => NameBox.Text.Trim();
    public string Abbreviation => CreateAbbreviation(UnitName, _existingAbbreviations);
    public bool AllowsFractional => FractionalBox.IsChecked == true;

    public ProductUnitCreateWindow(IEnumerable<ProductUnitInfo>? existingUnits = null)
    {
        InitializeComponent();
        _existingAbbreviations = (existingUnits ?? []).Select(x => x.Abbreviation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateAbbreviation(string name, IReadOnlySet<string> existingAbbreviations)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var compact = new string(decomposed
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            .ToArray()).Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (compact.Length == 0) compact = "unidad";

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var suffixText = suffix == 1 ? string.Empty : suffix.ToString(CultureInfo.InvariantCulture);
            var prefixLength = Math.Min(compact.Length, 12 - suffixText.Length);
            var candidate = compact[..prefixLength] + suffixText;
            if (!existingAbbreviations.Contains(candidate)) return candidate;
        }
        return compact[..Math.Min(compact.Length, 12)];
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (UnitName.Length is < 2 or > 48)
        {
            ErrorLabel.Text = "El nombre debe tener entre 2 y 48 caracteres.";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
