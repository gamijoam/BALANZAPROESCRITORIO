using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SaleHistoricalExchangeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedUsdToVesRate",
                table: "sales",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE sales AS sale
                SET "AppliedUsdToVesRate" = CASE
                    WHEN sale."Currency" = 'VES' THEN COALESCE(
                        (SELECT payment."AppliedExchangeRate" FROM sale_payments AS payment WHERE payment."SaleId" = sale."Id" AND payment."Currency" = 'USD' AND payment."AppliedExchangeRate" > 0 ORDER BY payment."Id" LIMIT 1),
                        (SELECT line."AppliedExchangeRate" FROM sale_lines AS line WHERE line."SaleId" = sale."Id" AND line."OriginalCurrency" = 'USD' AND line."AppliedExchangeRate" > 0 ORDER BY line."Id" LIMIT 1))
                    WHEN sale."Currency" = 'USD' THEN COALESCE(
                        1 / NULLIF((SELECT payment."AppliedExchangeRate" FROM sale_payments AS payment WHERE payment."SaleId" = sale."Id" AND payment."Currency" = 'VES' AND payment."AppliedExchangeRate" > 0 ORDER BY payment."Id" LIMIT 1), 0),
                        1 / NULLIF((SELECT line."AppliedExchangeRate" FROM sale_lines AS line WHERE line."SaleId" = sale."Id" AND line."OriginalCurrency" = 'VES' AND line."AppliedExchangeRate" > 0 ORDER BY line."Id" LIMIT 1), 0))
                    ELSE NULL
                END
                WHERE sale."AppliedUsdToVesRate" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedUsdToVesRate",
                table: "sales");
        }
    }
}
