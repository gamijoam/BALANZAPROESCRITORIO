using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20261014160000_Hito28PurchaseReceiptEffects")]
public partial class Hito28PurchaseReceiptEffects : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "purchase_receipt_effects",
            columns: table => new
            {
                receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                terminal_id = table.Column<Guid>(type: "uuid", nullable: false),
                applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_purchase_receipt_effects", x => x.receipt_id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "purchase_receipt_effects");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PurchaseReceiptEffectRecord>(entity =>
        {
            entity.ToTable("purchase_receipt_effects");
            entity.HasKey(value => value.ReceiptId);
            entity.Property(value => value.ReceiptId).HasColumnName("receipt_id");
            entity.Property(value => value.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64).IsRequired();
            entity.Property(value => value.ActorId).HasColumnName("actor_id");
            entity.Property(value => value.TerminalId).HasColumnName("terminal_id");
            entity.Property(value => value.AppliedAtUtc).HasColumnName("applied_at_utc").IsRequired();
        });
    }
}
