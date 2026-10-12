using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashSessionOperatorHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveOperatorId",
                table: "cash_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE cash_sessions SET \"ActiveOperatorId\" = \"OperatorId\" WHERE \"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_cash_sessions_ActiveOperatorId",
                table: "cash_sessions",
                column: "ActiveOperatorId");

            migrationBuilder.AddForeignKey(
                name: "FK_cash_sessions_operator_users_ActiveOperatorId",
                table: "cash_sessions",
                column: "ActiveOperatorId",
                principalTable: "operator_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cash_sessions_operator_users_ActiveOperatorId",
                table: "cash_sessions");

            migrationBuilder.DropIndex(
                name: "IX_cash_sessions_ActiveOperatorId",
                table: "cash_sessions");

            migrationBuilder.DropColumn(
                name: "ActiveOperatorId",
                table: "cash_sessions");
        }
    }
}
