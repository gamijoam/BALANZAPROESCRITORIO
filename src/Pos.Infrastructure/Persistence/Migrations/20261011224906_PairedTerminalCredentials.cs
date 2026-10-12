using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PairedTerminalCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paired_terminals",
                columns: table => new
                {
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PairedByOperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paired_terminals", x => x.TerminalId);
                    table.ForeignKey(
                        name: "FK_paired_terminals_operator_users_PairedByOperatorId",
                        column: x => x.PairedByOperatorId,
                        principalTable: "operator_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paired_terminals_Active",
                table: "paired_terminals",
                column: "Active");

            migrationBuilder.CreateIndex(
                name: "IX_paired_terminals_PairedByOperatorId",
                table: "paired_terminals",
                column: "PairedByOperatorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paired_terminals");
        }
    }
}
