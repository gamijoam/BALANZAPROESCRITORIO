using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20261014180000_Hito30RemoveRedundantPurchaseReceiptEffectLineIndex")]
public sealed class Hito30RemoveRedundantPurchaseReceiptEffectLineIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_purchase_receipt_effect_lines_receipt_id",
            table: "purchase_receipt_effect_lines");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_purchase_receipt_effect_lines_receipt_id",
            table: "purchase_receipt_effect_lines",
            column: "receipt_id");
    }
}
