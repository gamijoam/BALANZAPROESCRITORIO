using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TerminalClientVersionReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "terminal_client_versions",
                columns: table => new
                {
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastReportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminal_client_versions", x => x.TerminalId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "terminal_client_versions");
        }
    }
}
