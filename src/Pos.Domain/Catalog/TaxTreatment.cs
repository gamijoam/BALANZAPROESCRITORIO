namespace Pos.Domain.Catalog;

/// <summary>Explicit tax classification assigned to one catalog product.</summary>
public sealed record TaxTreatment
{
    public static TaxTreatment Unclassified { get; } = new("Unclassified", 0m, false);
    public static TaxTreatment Taxable16 { get; } = new("Taxable16", 0.16m, true);
    public static TaxTreatment Taxable8 { get; } = new("Taxable8", 0.08m, true);
    public static TaxTreatment Taxable0 { get; } = new("Taxable0", 0m, true);
    public static TaxTreatment Exempt { get; } = new("Exempt", 0m, true);

    public string Code { get; }
    public decimal Rate { get; }
    public bool IsClassified { get; }

    private TaxTreatment(string code, decimal rate, bool isClassified)
    {
        Code = code;
        Rate = rate;
        IsClassified = isClassified;
    }

    public static TaxTreatment FromCode(string? code) => code switch
    {
        "Unclassified" => Unclassified,
        "Taxable16" => Taxable16,
        "Taxable8" => Taxable8,
        "Taxable0" => Taxable0,
        "Exempt" => Exempt,
        _ => throw new ArgumentException("Unknown product tax treatment.", nameof(code))
    };
}
