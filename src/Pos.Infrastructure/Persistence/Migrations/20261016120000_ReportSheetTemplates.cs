using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20261016120000_ReportSheetTemplates")]
public partial class ReportSheetTemplates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "SheetJson",
            table: "operator_report_views",
            type: "jsonb",
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "SheetJson", table: "operator_report_views");
}
