using Pos.Domain.Catalog;
using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class ProductTaxTreatmentTests
{
    [Fact]
    public void Legacy_product_without_tax_classification_is_pending_and_cannot_be_sold()
    {
        var product = Product.Create("LEGACY-1", "Artículo antiguo", new Money(10m, Currency.USD));

        Assert.Equal(TaxTreatment.Unclassified, product.TaxTreatment);
        Assert.False(product.CanBeSold);
        Assert.Throws<DomainRuleException>(product.EnsureCanBeSold);
    }

    [Theory]
    [MemberData(nameof(ClassifiedTreatments))]
    public void Classified_product_can_be_sold_with_its_explicit_tax_rate(TaxTreatment treatment, decimal expectedRate)
    {
        var product = Product.Create("SKU-1", "Producto", new Money(10m, Currency.USD), treatment);

        Assert.True(product.CanBeSold);
        Assert.Equal(expectedRate, treatment.Rate);
        product.EnsureCanBeSold();
    }

    public static IEnumerable<object[]> ClassifiedTreatments =>
    [
        [TaxTreatment.Taxable16, 0.16m],
        [TaxTreatment.Taxable8, 0.08m],
        [TaxTreatment.Taxable0, 0m],
        [TaxTreatment.Exempt, 0m]
    ];

    [Fact]
    public void Zero_rated_and_exempt_treatments_remain_distinct()
    {
        Assert.NotEqual(TaxTreatment.Taxable0, TaxTreatment.Exempt);
        Assert.Equal(0m, TaxTreatment.Taxable0.Rate);
        Assert.Equal(0m, TaxTreatment.Exempt.Rate);
    }

    [Fact]
    public void Administrator_can_classify_a_pending_product_without_changing_its_prices()
    {
        var product = Product.Create("LEGACY-2", "Artículo", new Money(7m, Currency.VES));

        product.ChangeTaxTreatment(TaxTreatment.Taxable8);

        Assert.True(product.CanBeSold);
        Assert.Equal(TaxTreatment.Taxable8, product.TaxTreatment);
        Assert.Equal(new Money(7m, Currency.VES), product.Price);
    }
}
