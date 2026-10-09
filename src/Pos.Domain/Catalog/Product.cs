using Pos.Domain.Finance;

namespace Pos.Domain.Catalog;

public sealed class Product
{
    private readonly HashSet<string> _barcodes = new(StringComparer.OrdinalIgnoreCase);

    public string Sku { get; }
    public string Name { get; private set; }
    public Money Price { get; private set; }
    public bool IsActive { get; private set; } = true;
    public IReadOnlyCollection<string> Barcodes => _barcodes.ToArray();

    private Product(string sku, string name, Money price)
    {
        Sku = sku;
        Name = name;
        Price = price;
    }

    public static Product Create(string sku, string name, Money price)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("SKU is required.", nameof(sku));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Product name is required.", nameof(name));
        if (price.Amount < 0m) throw new DomainRuleException("Product price cannot be negative.");

        return new Product(sku.Trim(), name.Trim(), price);
    }

    public void AddBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) throw new ArgumentException("Barcode is required.", nameof(barcode));
        if (!_barcodes.Add(barcode.Trim())) throw new DomainRuleException("Barcode is already assigned to this product.");
    }

    public void ChangePrice(Money price)
    {
        if (price.Amount < 0m) throw new DomainRuleException("Product price cannot be negative.");
        if (price.Currency != Price.Currency) throw new DomainRuleException("Price currency cannot change without an explicit price-list operation.");
        Price = price;
    }

    public void Deactivate() => IsActive = false;
}
