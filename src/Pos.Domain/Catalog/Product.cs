using Pos.Domain.Finance;

namespace Pos.Domain.Catalog;

public sealed class Product
{
    private readonly HashSet<string> _barcodes = new(StringComparer.OrdinalIgnoreCase);

    public string Sku { get; }
    public string Name { get; private set; }
    public ProductPriceList Prices { get; private set; }
    public Money Price => Prices.Price1;
    public TaxTreatment TaxTreatment { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool CanBeSold => IsActive && TaxTreatment.IsClassified;
    public IReadOnlyCollection<string> Barcodes => _barcodes.ToArray();

    private Product(string sku, string name, ProductPriceList prices, TaxTreatment taxTreatment)
    {
        Sku = sku;
        Name = name;
        Prices = prices;
        TaxTreatment = taxTreatment;
    }

    public static Product Create(string sku, string name, Money price, TaxTreatment? taxTreatment = null)
    {
        return Create(sku, name, ProductPriceList.Create(price), taxTreatment);
    }

    public static Product Create(string sku, string name, ProductPriceList prices, TaxTreatment? taxTreatment = null)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("SKU is required.", nameof(sku));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Product name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(prices);

        return new Product(sku.Trim(), name.Trim(), prices, taxTreatment ?? TaxTreatment.Unclassified);
    }

    public void AddBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) throw new ArgumentException("Barcode is required.", nameof(barcode));
        if (!_barcodes.Add(barcode.Trim())) throw new DomainRuleException("Barcode is already assigned to this product.");
    }

    public void ChangePrice(Money price)
    {
        ChangePrice(PriceTier.Price1, price);
    }

    public void ChangePrice(PriceTier tier, Money price) => Prices = Prices.WithPrice(tier, price);

    public void ChangeTaxTreatment(TaxTreatment taxTreatment)
    {
        ArgumentNullException.ThrowIfNull(taxTreatment);
        TaxTreatment = taxTreatment;
    }

    public void EnsureCanBeSold()
    {
        if (!IsActive) throw new DomainRuleException("Inactive products cannot be sold.");
        if (!TaxTreatment.IsClassified) throw new DomainRuleException("Product tax treatment must be classified before it can be sold.");
    }

    public void Deactivate() => IsActive = false;
}
