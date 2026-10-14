using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hito27MarkHistoricalTaxAsNotCaptured : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE sale_lines SET \"TaxTreatment\" = 'NotCaptured' WHERE \"TaxTreatment\" = 'Unclassified'");
            migrationBuilder.AlterColumn<string>(
                name: "TaxTreatment",
                table: "sale_lines",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "NotCaptured",
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24,
                oldDefaultValue: "Unclassified");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE sale_lines SET \"TaxTreatment\" = 'Unclassified' WHERE \"TaxTreatment\" = 'NotCaptured'");
            migrationBuilder.AlterColumn<string>(
                name: "TaxTreatment",
                table: "sale_lines",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unclassified",
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24,
                oldDefaultValue: "NotCaptured");
        }
    }
}
