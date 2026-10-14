using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pos.Infrastructure.Persistence;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20261014170000_Hito29ReversiblePurchaseReceipts")]
public partial class Hito29ReversiblePurchaseReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "reversed_at_utc", table: "purchase_receipt_effects", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "reversed_by_actor_id", table: "purchase_receipt_effects", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "reversed_by_terminal_id", table: "purchase_receipt_effects", type: "uuid", nullable: true);
        migrationBuilder.CreateTable(
            name: "purchase_receipt_effect_lines",
            columns: table => new
            {
                receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                prior_stock_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                prior_unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                unit_cost_after = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                quantity_after_receipt = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                warehouse_id = table.Column<Guid>(type: "uuid", nullable: true),
                movement_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_purchase_receipt_effect_lines", x => new { x.receipt_id, x.product_id });
                table.ForeignKey(name: "FK_purchase_receipt_effect_lines_purchase_receipt_effects_receipt_id",
                    column: x => x.receipt_id, principalTable: "purchase_receipt_effects", principalColumn: "receipt_id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex(name: "IX_purchase_receipt_effect_lines_receipt_id",
            table: "purchase_receipt_effect_lines", column: "receipt_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "purchase_receipt_effect_lines");
        migrationBuilder.DropColumn(name: "reversed_at_utc", table: "purchase_receipt_effects");
        migrationBuilder.DropColumn(name: "reversed_by_actor_id", table: "purchase_receipt_effects");
        migrationBuilder.DropColumn(name: "reversed_by_terminal_id", table: "purchase_receipt_effects");
    }

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
            entity.Property(value => value.ReversedAtUtc).HasColumnName("reversed_at_utc");
            entity.Property(value => value.ReversedByActorId).HasColumnName("reversed_by_actor_id");
            entity.Property(value => value.ReversedByTerminalId).HasColumnName("reversed_by_terminal_id");
        });
        modelBuilder.Entity<PurchaseReceiptEffectLineRecord>(entity =>
        {
            entity.ToTable("purchase_receipt_effect_lines");
            entity.HasKey(value => new { value.ReceiptId, value.ProductId });
            entity.Property(value => value.ReceiptId).HasColumnName("receipt_id");
            entity.Property(value => value.ProductId).HasColumnName("product_id");
            entity.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
            entity.Property(value => value.PriorStockQuantity).HasColumnName("prior_stock_quantity").HasPrecision(18, 4);
            entity.Property(value => value.PriorUnitCost).HasColumnName("prior_unit_cost").HasPrecision(18, 4);
            entity.Property(value => value.UnitCostAfter).HasColumnName("unit_cost_after").HasPrecision(18, 4);
            entity.Property(value => value.QuantityAfterReceipt).HasColumnName("quantity_after_receipt").HasPrecision(18, 4);
            entity.Property(value => value.WarehouseId).HasColumnName("warehouse_id");
            entity.Property(value => value.MovementId).HasColumnName("movement_id");
            entity.HasOne<PurchaseReceiptEffectRecord>().WithMany().HasForeignKey(value => value.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
