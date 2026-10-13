using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20261012130000_OperatorReportViews")]
public sealed class OperatorReportViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "operator_report_views",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                ReportId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ColumnsJson = table.Column<string>(type: "jsonb", nullable: false),
                FiltersJson = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operator_report_views", x => x.Id);
                table.ForeignKey("FK_operator_report_views_operator_users_OperatorId", x => x.OperatorId,
                    "operator_users", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_operator_report_views_OperatorId_ReportId_Name",
            "operator_report_views", new[] { "OperatorId", "ReportId", "Name" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "operator_report_views");
    }
}
