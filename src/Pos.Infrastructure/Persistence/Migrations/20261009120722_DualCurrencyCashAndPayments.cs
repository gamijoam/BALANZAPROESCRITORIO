using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DualCurrencyCashAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedExchangeRate",
                table: "sale_payments",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExchangeRateId",
                table: "sale_payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CountedClosingBalanceUsd",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CountedClosingBalanceVes",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedClosingBalanceUsd",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedClosingBalanceVes",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OpeningBalanceUsd",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OpeningBalanceVes",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VarianceUsd",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VarianceVes",
                table: "cash_sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "cash_movements",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "VES");

            migrationBuilder.Sql("UPDATE cash_sessions SET \"OpeningBalanceUsd\" = CASE WHEN \"Currency\" = 'USD' THEN \"OpeningBalance\" ELSE 0 END, \"OpeningBalanceVes\" = CASE WHEN \"Currency\" = 'VES' THEN \"OpeningBalance\" ELSE 0 END, \"ExpectedClosingBalanceUsd\" = CASE WHEN \"Currency\" = 'USD' THEN \"ExpectedClosingBalance\" END, \"CountedClosingBalanceUsd\" = CASE WHEN \"Currency\" = 'USD' THEN \"CountedClosingBalance\" END, \"VarianceUsd\" = CASE WHEN \"Currency\" = 'USD' THEN \"Variance\" END, \"ExpectedClosingBalanceVes\" = CASE WHEN \"Currency\" = 'VES' THEN \"ExpectedClosingBalance\" END, \"CountedClosingBalanceVes\" = CASE WHEN \"Currency\" = 'VES' THEN \"CountedClosingBalance\" END, \"VarianceVes\" = CASE WHEN \"Currency\" = 'VES' THEN \"Variance\" END");
            migrationBuilder.Sql("UPDATE cash_movements AS movement SET \"Currency\" = session.\"Currency\" FROM cash_sessions AS session WHERE movement.\"CashSessionId\" = session.\"Id\"");

            migrationBuilder.CreateIndex(
                name: "IX_sale_payments_ExchangeRateId",
                table: "sale_payments",
                column: "ExchangeRateId");

            migrationBuilder.AddForeignKey(
                name: "FK_sale_payments_exchange_rates_ExchangeRateId",
                table: "sale_payments",
                column: "ExchangeRateId",
                principalTable: "exchange_rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sale_payments_exchange_rates_ExchangeRateId",
                table: "sale_payments");

            migrationBuilder.DropIndex(
                name: "IX_sale_payments_ExchangeRateId",
                table: "sale_payments");

            migrationBuilder.DropColumn(
                name: "AppliedExchangeRate",
                table: "sale_payments");

            migrationBuilder.DropColumn(
                name: "ExchangeRateId",
                table: "sale_payments");

            migrationBuilder.DropColumn(
                name: "CountedClosingBalanceUsd",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "CountedClosingBalanceVes",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "ExpectedClosingBalanceUsd",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "ExpectedClosingBalanceVes",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "OpeningBalanceUsd",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "OpeningBalanceVes",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "VarianceUsd",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "VarianceVes",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "cash_movements");
        }
    }
}
