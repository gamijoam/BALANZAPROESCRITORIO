using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20261016180000_Hito36ProductUnitsAndPresentations")]
public partial class Hito36ProductUnitsAndPresentations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "product_units",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                Abbreviation = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                AllowsFractionalQuantities = table.Column<bool>(type: "boolean", nullable: false),
                IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_product_units", x => x.Id));

        var seededAt = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        migrationBuilder.InsertData(
            table: "product_units",
            columns: new[] { "Id", "Name", "Abbreviation", "AllowsFractionalQuantities", "IsSystem", "Active", "CreatedAtUtc" },
            values: new object[,]
            {
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240001"), "Unidad", "ud", true, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240002"), "Par", "par", false, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240003"), "Caja", "caja", false, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240004"), "Docena", "doc", false, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240005"), "Kilogramo", "kg", true, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240006"), "Gramo", "g", true, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240007"), "Litro", "L", true, true, true, seededAt },
                { new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240008"), "Metro", "m", true, true, true, seededAt }
            });

        migrationBuilder.CreateIndex(name: "IX_product_units_Name", table: "product_units", column: "Name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_product_units_Abbreviation", table: "product_units", column: "Abbreviation", unique: true);

        migrationBuilder.AddColumn<Guid>(
            name: "BaseUnitId",
            table: "products",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("6ef79e43-cbf6-4d79-ae5b-ef5533240001"));

        migrationBuilder.CreateIndex(name: "IX_products_BaseUnitId", table: "products", column: "BaseUnitId");
        migrationBuilder.AddForeignKey(
            name: "FK_products_product_units_BaseUnitId",
            table: "products",
            column: "BaseUnitId",
            principalTable: "product_units",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.CreateTable(
            name: "product_presentations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ConversionFactor = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                Price1 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                Price2 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                Price3 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                Barcode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_presentations", x => x.Id);
                table.ForeignKey("FK_product_presentations_products_ProductId", x => x.ProductId,
                    principalTable: "products", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_product_presentations_product_units_UnitId", x => x.UnitId,
                    principalTable: "product_units", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_product_presentations_ProductId_Label", table: "product_presentations",
            columns: new[] { "ProductId", "Label" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_product_presentations_UnitId", table: "product_presentations", column: "UnitId");
        migrationBuilder.CreateIndex(name: "IX_product_presentations_Barcode", table: "product_presentations", column: "Barcode",
            unique: true, filter: "\"Barcode\" IS NOT NULL");

        migrationBuilder.AddColumn<Guid>(name: "PresentationId", table: "sale_lines", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "PresentationName", table: "sale_lines", type: "character varying(80)", maxLength: 80,
            nullable: false, defaultValue: "Unidad");
        migrationBuilder.AddColumn<decimal>(name: "ConversionFactor", table: "sale_lines", type: "numeric(18,3)", precision: 18, scale: 3,
            nullable: false, defaultValue: 1m);
        migrationBuilder.AddColumn<decimal>(name: "InventoryQuantity", table: "sale_lines", type: "numeric(18,3)", precision: 18, scale: 3,
            nullable: false, defaultValue: 0m);
        migrationBuilder.Sql("UPDATE sale_lines SET \"InventoryQuantity\" = \"Quantity\"");
        migrationBuilder.CreateIndex(name: "IX_sale_lines_PresentationId", table: "sale_lines", column: "PresentationId");
        migrationBuilder.AddForeignKey(name: "FK_sale_lines_product_presentations_PresentationId", table: "sale_lines",
            column: "PresentationId", principalTable: "product_presentations", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_sale_lines_product_presentations_PresentationId", table: "sale_lines");
        migrationBuilder.DropIndex(name: "IX_sale_lines_PresentationId", table: "sale_lines");
        migrationBuilder.DropColumn(name: "PresentationId", table: "sale_lines");
        migrationBuilder.DropColumn(name: "PresentationName", table: "sale_lines");
        migrationBuilder.DropColumn(name: "ConversionFactor", table: "sale_lines");
        migrationBuilder.DropColumn(name: "InventoryQuantity", table: "sale_lines");
        migrationBuilder.DropTable(name: "product_presentations");
        migrationBuilder.DropForeignKey(name: "FK_products_product_units_BaseUnitId", table: "products");
        migrationBuilder.DropIndex(name: "IX_products_BaseUnitId", table: "products");
        migrationBuilder.DropColumn(name: "BaseUnitId", table: "products");
        migrationBuilder.DropTable(name: "product_units");
    }
}
