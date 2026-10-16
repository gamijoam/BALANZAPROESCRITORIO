using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hito35StockMinimumAndReceivableDueDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "receivable_entries",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumStock",
                table: "products",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "receivable_entries");

            migrationBuilder.DropColumn(
                name: "MinimumStock",
                table: "products");
        }
    }
}
