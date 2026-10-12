using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hito12ControlledReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_returns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CashSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_returns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_returns_cash_sessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalTable: "cash_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_returns_sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_return_idempotency",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SaleReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_return_idempotency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_return_idempotency_sale_returns_SaleReturnId",
                        column: x => x.SaleReturnId,
                        principalTable: "sale_returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_return_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Restock = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_return_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_return_lines_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_return_lines_sale_lines_SaleLineId",
                        column: x => x.SaleLineId,
                        principalTable: "sale_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_return_lines_sale_returns_SaleReturnId",
                        column: x => x.SaleReturnId,
                        principalTable: "sale_returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_return_payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalPaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AppliedExchangeRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_return_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_return_payments_sale_payments_OriginalPaymentId",
                        column: x => x.OriginalPaymentId,
                        principalTable: "sale_payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_return_payments_sale_returns_SaleReturnId",
                        column: x => x.SaleReturnId,
                        principalTable: "sale_returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_idempotency_SaleReturnId",
                table: "sale_return_idempotency",
                column: "SaleReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_idempotency_TerminalId_Key",
                table: "sale_return_idempotency",
                columns: new[] { "TerminalId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_lines_ProductId",
                table: "sale_return_lines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_lines_SaleLineId",
                table: "sale_return_lines",
                column: "SaleLineId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_lines_SaleReturnId",
                table: "sale_return_lines",
                column: "SaleReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_payments_OriginalPaymentId",
                table: "sale_return_payments",
                column: "OriginalPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_payments_SaleReturnId",
                table: "sale_return_payments",
                column: "SaleReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_returns_CashSessionId",
                table: "sale_returns",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_returns_SaleId_CreatedAtUtc",
                table: "sale_returns",
                columns: new[] { "SaleId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_return_idempotency");

            migrationBuilder.DropTable(
                name: "sale_return_lines");

            migrationBuilder.DropTable(
                name: "sale_return_payments");

            migrationBuilder.DropTable(
                name: "sale_returns");
        }
    }
}
