using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MixedCurrencySaleSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedExchangeRate",
                table: "sale_lines",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExchangeRateId",
                table: "sale_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalCurrency",
                table: "sale_lines",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalUnitPrice",
                table: "sale_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_lines_ExchangeRateId",
                table: "sale_lines",
                column: "ExchangeRateId");

            migrationBuilder.AddForeignKey(
                name: "FK_sale_lines_exchange_rates_ExchangeRateId",
                table: "sale_lines",
                column: "ExchangeRateId",
                principalTable: "exchange_rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sale_lines_exchange_rates_ExchangeRateId",
                table: "sale_lines");

            migrationBuilder.DropIndex(
                name: "IX_sale_lines_ExchangeRateId",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "AppliedExchangeRate",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "ExchangeRateId",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "OriginalCurrency",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "OriginalUnitPrice",
                table: "sale_lines");
        }
    }
}
