using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hito24ProductTaxAndPriceTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CostCurrency",
                table: "sale_lines",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceTier",
                table: "sale_lines",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Price1");

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "sale_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRate",
                table: "sale_lines",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxTreatment",
                table: "sale_lines",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unclassified");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "sale_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Price2",
                table: "products",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Price3",
                table: "products",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxTreatment",
                table: "products",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unclassified");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "products",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostCurrency",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "PriceTier",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "TaxTreatment",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "Price2",
                table: "products");

            migrationBuilder.DropColumn(
                name: "Price3",
                table: "products");

            migrationBuilder.DropColumn(
                name: "TaxTreatment",
                table: "products");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "products");
        }
    }
}
