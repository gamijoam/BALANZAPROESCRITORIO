using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

public partial class Hito31CartWorkspaces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "cart_workspaces",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                SaleId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                Note = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                LinesJson = table.Column<string>(type: "jsonb", nullable: false),
                TotalAtPause = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastMutationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                LastMutationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cart_workspaces", x => x.Id);
                table.ForeignKey("FK_cart_workspaces_customers_CustomerId", x => x.CustomerId,
                    principalTable: "customers", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_cart_workspaces_operator_users_OperatorId", x => x.OperatorId,
                    principalTable: "operator_users", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_cart_workspaces_sales_SaleId", x => x.SaleId,
                    principalTable: "sales", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_cart_workspaces_CustomerId", table: "cart_workspaces", column: "CustomerId");
        migrationBuilder.CreateIndex(name: "IX_cart_workspaces_OperatorId", table: "cart_workspaces", column: "OperatorId");
        migrationBuilder.CreateIndex(name: "IX_cart_workspaces_SaleId", table: "cart_workspaces", column: "SaleId");
        migrationBuilder.CreateIndex(name: "IX_cart_workspaces_TerminalId", table: "cart_workspaces", column: "TerminalId",
            unique: true, filter: "\"Status\" = 'Active'");
        migrationBuilder.CreateIndex(name: "IX_cart_workspaces_TerminalId_Status_UpdatedAtUtc", table: "cart_workspaces",
            columns: new[] { "TerminalId", "Status", "UpdatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "cart_workspaces");
}
