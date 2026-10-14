using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hito23ReceivableOpeningBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "receivable_entries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOpeningBalance",
                table: "receivable_entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_receivable_entries_CustomerId",
                table: "receivable_entries",
                column: "CustomerId",
                unique: true,
                filter: "\"IsOpeningBalance\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_receivable_entries_IdempotencyKey",
                table: "receivable_entries",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_receivable_entries_opening_balance_charge",
                table: "receivable_entries",
                sql: "\"IsOpeningBalance\" = FALSE OR (\"EntryType\" = 'Charge' AND \"SaleId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_receivable_entries_CustomerId",
                table: "receivable_entries");

            migrationBuilder.DropIndex(
                name: "IX_receivable_entries_IdempotencyKey",
                table: "receivable_entries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_receivable_entries_opening_balance_charge",
                table: "receivable_entries");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "receivable_entries");

            migrationBuilder.DropColumn(
                name: "IsOpeningBalance",
                table: "receivable_entries");
        }
    }
}
