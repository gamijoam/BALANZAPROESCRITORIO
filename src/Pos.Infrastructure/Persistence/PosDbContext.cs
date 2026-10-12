using Microsoft.EntityFrameworkCore;

namespace Pos.Infrastructure.Persistence;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    public DbSet<ProductRecord> Products => Set<ProductRecord>();
    public DbSet<StockRecord> Stocks => Set<StockRecord>();
    public DbSet<SaleRecord> Sales => Set<SaleRecord>();
    public DbSet<SaleLineRecord> SaleLines => Set<SaleLineRecord>();
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditRecord> AuditEvents => Set<AuditRecord>();
    public DbSet<OperatorUserRecord> OperatorUsers => Set<OperatorUserRecord>();
    public DbSet<OperatorPermissionOverrideRecord> OperatorPermissionOverrides => Set<OperatorPermissionOverrideRecord>();
    public DbSet<AccessSessionRecord> AccessSessions => Set<AccessSessionRecord>();
    public DbSet<CashSessionRecord> CashSessions => Set<CashSessionRecord>();
    public DbSet<StockMovementRecord> StockMovements => Set<StockMovementRecord>();
    public DbSet<CashMovementRecord> CashMovements => Set<CashMovementRecord>();
    public DbSet<PriceHistoryRecord> PriceHistory => Set<PriceHistoryRecord>();
    public DbSet<ExchangeRateRecord> ExchangeRates => Set<ExchangeRateRecord>();
    public DbSet<CustomerRecord> Customers => Set<CustomerRecord>();
    public DbSet<ReceivableEntryRecord> ReceivableEntries => Set<ReceivableEntryRecord>();
    public DbSet<SaleReturnRecord> SaleReturns => Set<SaleReturnRecord>();
    public DbSet<SaleReturnLineRecord> SaleReturnLines => Set<SaleReturnLineRecord>();
    public DbSet<SaleReturnPaymentRecord> SaleReturnPayments => Set<SaleReturnPaymentRecord>();
    public DbSet<ReturnIdempotencyRecord> ReturnIdempotencyRecords => Set<ReturnIdempotencyRecord>();
    public DbSet<PairedTerminalRecord> PairedTerminals => Set<PairedTerminalRecord>();
    public DbSet<TerminalClientVersionRecord> TerminalClientVersions => Set<TerminalClientVersionRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ProductRecord>(e => {
            e.ToTable("products"); e.HasKey(x => x.Id); e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Sku).IsUnique(); e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.UnitPrice).HasPrecision(18, 4); e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.ImageFileName).HasMaxLength(80);
        });
        model.Entity<StockRecord>(e => {
            e.ToTable("stocks"); e.HasKey(x => x.ProductId); e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.HasOne<ProductRecord>().WithOne().HasForeignKey<StockRecord>(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        model.Entity<SaleRecord>(e => {
            e.ToTable("sales"); e.HasKey(x => x.Id); e.Property(x => x.Total).HasPrecision(18, 4);
            e.Property(x => x.RoundingAdjustment).HasPrecision(18, 4);
            e.Property(x => x.AppliedUsdToVesRate).HasPrecision(18, 8);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasOne<CustomerRecord>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SaleLineRecord>(e => {
            e.ToTable("sale_lines"); e.HasKey(x => x.Id); e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(200).IsRequired(); e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4); e.Property(x => x.LineTotal).HasPrecision(18, 4);
            e.Property(x => x.OriginalUnitPrice).HasPrecision(18, 4); e.Property(x => x.OriginalCurrency).HasMaxLength(3);
            e.Property(x => x.AppliedExchangeRate).HasPrecision(18, 8);
            e.HasOne<ExchangeRateRecord>().WithMany().HasForeignKey(x => x.ExchangeRateId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SaleRecord>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PaymentRecord>(e => {
            e.ToTable("sale_payments"); e.HasKey(x => x.Id); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Method).HasMaxLength(32).IsRequired();
            e.HasOne<SaleRecord>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.AppliedExchangeRate).HasPrecision(18, 8);
            e.HasOne<ExchangeRateRecord>().WithMany().HasForeignKey(x => x.ExchangeRateId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<IdempotencyRecord>(e => {
            e.ToTable("idempotency_records"); e.HasKey(x => x.Id); e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired(); e.HasIndex(x => new { x.TerminalId, x.Key }).IsUnique();
            e.HasOne<SaleRecord>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AuditRecord>(e => {
            e.ToTable("audit_events"); e.HasKey(x => x.Id); e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb"); e.HasIndex(x => x.OccurredAtUtc);
        });
        model.Entity<OperatorUserRecord>(e => {
            e.ToTable("operator_users"); e.HasKey(x => x.Id); e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Username).IsUnique(); e.Property(x => x.PinSalt).IsRequired(); e.Property(x => x.PinHash).IsRequired();
            e.Property(x => x.Role).HasMaxLength(24).HasDefaultValue("Administrator").IsRequired();
        });
        model.Entity<OperatorPermissionOverrideRecord>(e => {
            e.ToTable("operator_permission_overrides"); e.HasKey(x => x.Id);
            e.Property(x => x.Permission).HasMaxLength(64).IsRequired(); e.Property(x => x.Mode).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.OperatorId, x.Permission }).IsUnique();
            e.HasOne<OperatorUserRecord>().WithMany().HasForeignKey(x => x.OperatorId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AccessSessionRecord>(e => {
            e.ToTable("access_sessions"); e.HasKey(x => x.Id); e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => new { x.OperatorId, x.ExpiresAtUtc });
            e.HasOne<OperatorUserRecord>().WithMany().HasForeignKey(x => x.OperatorId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CashSessionRecord>(e => {
            e.ToTable("cash_sessions"); e.HasKey(x => x.Id); e.Property(x => x.OpeningBalance).HasPrecision(18, 4);
            e.Property(x => x.ExpectedClosingBalance).HasPrecision(18, 4); e.Property(x => x.CountedClosingBalance).HasPrecision(18, 4); e.Property(x => x.Variance).HasPrecision(18, 4);
            e.Property(x => x.OpeningBalanceUsd).HasPrecision(18, 4); e.Property(x => x.OpeningBalanceVes).HasPrecision(18, 4);
            e.Property(x => x.ExpectedClosingBalanceUsd).HasPrecision(18, 4); e.Property(x => x.CountedClosingBalanceUsd).HasPrecision(18, 4); e.Property(x => x.VarianceUsd).HasPrecision(18, 4);
            e.Property(x => x.ExpectedClosingBalanceVes).HasPrecision(18, 4); e.Property(x => x.CountedClosingBalanceVes).HasPrecision(18, 4); e.Property(x => x.VarianceVes).HasPrecision(18, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.HasOne<OperatorUserRecord>().WithMany().HasForeignKey(x => x.ActiveOperatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TerminalId).IsUnique().HasFilter("\"Status\" = 'Open'"); e.HasIndex(x => x.OperatorId);
        });
        model.Entity<StockMovementRecord>(e => {
            e.ToTable("stock_movements"); e.HasKey(x => x.Id); e.Property(x => x.QuantityDelta).HasPrecision(18, 3);
            e.Property(x => x.Reason).HasMaxLength(250).IsRequired(); e.HasIndex(x => new { x.ProductId, x.OccurredAtUtc });
            e.HasOne<ProductRecord>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CashMovementRecord>(e => {
            e.ToTable("cash_movements"); e.HasKey(x => x.Id); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Direction).HasMaxLength(8).IsRequired(); e.Property(x => x.Reason).HasMaxLength(250).IsRequired();
            e.HasIndex(x => new { x.CashSessionId, x.OccurredAtUtc });
            e.HasOne<CashSessionRecord>().WithMany().HasForeignKey(x => x.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PriceHistoryRecord>(e => {
            e.ToTable("price_history"); e.HasKey(x => x.Id); e.Property(x => x.OldPrice).HasPrecision(18, 4); e.Property(x => x.NewPrice).HasPrecision(18, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Reason).HasMaxLength(250).IsRequired();
            e.HasIndex(x => new { x.ProductId, x.ChangedAtUtc }); e.HasOne<ProductRecord>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ExchangeRateRecord>(e => {
            e.ToTable("exchange_rates"); e.HasKey(x => x.Id); e.Property(x => x.FromCurrency).HasMaxLength(3).IsRequired();
            e.Property(x => x.ToCurrency).HasMaxLength(3).IsRequired(); e.Property(x => x.Rate).HasPrecision(18, 8); e.Property(x => x.Source).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.FromCurrency, x.ToCurrency, x.EffectiveAtUtc }).IsUnique();
        });
        model.Entity<CustomerRecord>(e => {
            e.ToTable("customers"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.DocumentNumber).HasMaxLength(64); e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.CreditLimit).HasPrecision(18, 4);
            e.HasIndex(x => x.DocumentNumber).IsUnique().HasFilter("\"DocumentNumber\" IS NOT NULL");
        });
        model.Entity<ReceivableEntryRecord>(e => {
            e.ToTable("receivable_entries"); e.HasKey(x => x.Id); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.OriginalAmount).HasPrecision(18, 4); e.Property(x => x.AppliedExchangeRate).HasPrecision(18, 8);
            e.Property(x => x.OriginalCurrency).HasMaxLength(3);
            e.Property(x => x.EntryType).HasMaxLength(16).IsRequired(); e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Reason).HasMaxLength(250).IsRequired();
            e.HasIndex(x => new { x.CustomerId, x.OccurredAtUtc }); e.HasOne<CustomerRecord>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SaleRecord>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SaleReturnRecord>(e => {
            e.ToTable("sale_returns"); e.HasKey(x => x.Id); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Reason).HasMaxLength(250).IsRequired();
            e.HasIndex(x => new { x.SaleId, x.CreatedAtUtc });
            e.HasOne<SaleRecord>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CashSessionRecord>().WithMany().HasForeignKey(x => x.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SaleReturnLineRecord>(e => {
            e.ToTable("sale_return_lines"); e.HasKey(x => x.Id); e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.HasOne<SaleReturnRecord>().WithMany().HasForeignKey(x => x.SaleReturnId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SaleLineRecord>().WithMany().HasForeignKey(x => x.SaleLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductRecord>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SaleReturnPaymentRecord>(e => {
            e.ToTable("sale_return_payments"); e.HasKey(x => x.Id); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired(); e.Property(x => x.Method).HasMaxLength(32).IsRequired(); e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.AppliedExchangeRate).HasPrecision(18, 8);
            e.HasOne<SaleReturnRecord>().WithMany().HasForeignKey(x => x.SaleReturnId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PaymentRecord>().WithMany().HasForeignKey(x => x.OriginalPaymentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ReturnIdempotencyRecord>(e => {
            e.ToTable("sale_return_idempotency"); e.HasKey(x => x.Id); e.Property(x => x.Key).HasMaxLength(128).IsRequired(); e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.TerminalId, x.Key }).IsUnique();
            e.HasOne<SaleReturnRecord>().WithMany().HasForeignKey(x => x.SaleReturnId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PairedTerminalRecord>(e => {
            e.ToTable("paired_terminals"); e.HasKey(x => x.TerminalId);
            e.Property(x => x.CredentialHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.PairedAtUtc).IsRequired(); e.HasIndex(x => x.Active);
            e.HasOne<OperatorUserRecord>().WithMany().HasForeignKey(x => x.PairedByOperatorId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TerminalClientVersionRecord>(e => {
            e.ToTable("terminal_client_versions"); e.HasKey(x => x.TerminalId);
            e.Property(x => x.ClientVersion).HasMaxLength(32);
            e.Property(x => x.LastReportedAtUtc).IsRequired();
        });
    }
}

public sealed class ProductRecord { public Guid Id { get; set; } public string Sku { get; set; } = ""; public string Name { get; set; } = ""; public decimal UnitPrice { get; set; } public string Currency { get; set; } = "VES"; public string? ImageFileName { get; set; } public bool Active { get; set; } = true; public DateTime CreatedAtUtc { get; set; } }
public sealed class StockRecord { public Guid ProductId { get; set; } public decimal Quantity { get; set; } public long Version { get; set; } }
public sealed class SaleRecord { public Guid Id { get; set; } public Guid TerminalId { get; set; } public Guid CashSessionId { get; set; } public Guid OperatorId { get; set; } public Guid? CustomerId { get; set; } public decimal Total { get; set; } public decimal RoundingAdjustment { get; set; } public decimal? AppliedUsdToVesRate { get; set; } public string Currency { get; set; } = "VES"; public string Status { get; set; } = "Confirmed"; public DateTime CreatedAtUtc { get; set; } }
public sealed class SaleLineRecord { public Guid Id { get; set; } public Guid SaleId { get; set; } public Guid ProductId { get; set; } public string Sku { get; set; } = ""; public string Description { get; set; } = ""; public decimal Quantity { get; set; } public decimal UnitPrice { get; set; } public decimal LineTotal { get; set; } public decimal? OriginalUnitPrice { get; set; } public string? OriginalCurrency { get; set; } public Guid? ExchangeRateId { get; set; } public decimal? AppliedExchangeRate { get; set; } }
public sealed class PaymentRecord { public Guid Id { get; set; } public Guid SaleId { get; set; } public string Method { get; set; } = ""; public decimal Amount { get; set; } public string Currency { get; set; } = "VES"; public Guid? ExchangeRateId { get; set; } public decimal? AppliedExchangeRate { get; set; } }
public sealed class IdempotencyRecord { public Guid Id { get; set; } public Guid TerminalId { get; set; } public string Key { get; set; } = ""; public string PayloadHash { get; set; } = ""; public Guid SaleId { get; set; } public DateTime CreatedAtUtc { get; set; } }
public sealed class AuditRecord { public Guid Id { get; set; } public Guid ActorId { get; set; } public Guid TerminalId { get; set; } public string Action { get; set; } = ""; public string Payload { get; set; } = "{}"; public DateTime OccurredAtUtc { get; set; } }
public sealed class OperatorUserRecord { public Guid Id { get; set; } public string Username { get; set; } = ""; public string Role { get; set; } = "Administrator"; public byte[] PinSalt { get; set; } = []; public byte[] PinHash { get; set; } = []; public int FailedLoginCount { get; set; } public DateTime? LockoutUntilUtc { get; set; } public bool Active { get; set; } = true; public DateTime CreatedAtUtc { get; set; } }
public sealed class OperatorPermissionOverrideRecord { public Guid Id { get; set; } public Guid OperatorId { get; set; } public string Permission { get; set; } = ""; public string Mode { get; set; } = "Inherit"; public Guid UpdatedByOperatorId { get; set; } public DateTime UpdatedAtUtc { get; set; } }
public sealed class AccessSessionRecord { public Guid Id { get; set; } public Guid OperatorId { get; set; } public Guid TerminalId { get; set; } public string TokenHash { get; set; } = ""; public DateTime CreatedAtUtc { get; set; } public DateTime ExpiresAtUtc { get; set; } public DateTime? RevokedAtUtc { get; set; } }
public sealed class CashSessionRecord { public Guid Id { get; set; } public Guid OperatorId { get; set; } public Guid? ActiveOperatorId { get; set; } public Guid TerminalId { get; set; } public decimal OpeningBalance { get; set; } public decimal? ExpectedClosingBalance { get; set; } public decimal? CountedClosingBalance { get; set; } public decimal? Variance { get; set; } public decimal OpeningBalanceUsd { get; set; } public decimal OpeningBalanceVes { get; set; } public decimal? ExpectedClosingBalanceUsd { get; set; } public decimal? CountedClosingBalanceUsd { get; set; } public decimal? VarianceUsd { get; set; } public decimal? ExpectedClosingBalanceVes { get; set; } public decimal? CountedClosingBalanceVes { get; set; } public decimal? VarianceVes { get; set; } public string Currency { get; set; } = "VES"; public string Status { get; set; } = "Open"; public DateTime OpenedAtUtc { get; set; } public DateTime? ClosedAtUtc { get; set; } }
public sealed class StockMovementRecord { public Guid Id { get; set; } public Guid ProductId { get; set; } public decimal QuantityDelta { get; set; } public string Reason { get; set; } = ""; public Guid ActorId { get; set; } public Guid TerminalId { get; set; } public DateTime OccurredAtUtc { get; set; } }
public sealed class CashMovementRecord { public Guid Id { get; set; } public Guid CashSessionId { get; set; } public decimal Amount { get; set; } public string Currency { get; set; } = "VES"; public string Direction { get; set; } = "In"; public string Reason { get; set; } = ""; public Guid ActorId { get; set; } public DateTime OccurredAtUtc { get; set; } }
public sealed class PriceHistoryRecord { public Guid Id { get; set; } public Guid ProductId { get; set; } public decimal OldPrice { get; set; } public decimal NewPrice { get; set; } public string Currency { get; set; } = "VES"; public string Reason { get; set; } = ""; public Guid ActorId { get; set; } public DateTime ChangedAtUtc { get; set; } }
public sealed class ExchangeRateRecord { public Guid Id { get; set; } public string FromCurrency { get; set; } = "USD"; public string ToCurrency { get; set; } = "VES"; public decimal Rate { get; set; } public DateTime EffectiveAtUtc { get; set; } public string Source { get; set; } = ""; public Guid ActorId { get; set; } public DateTime CreatedAtUtc { get; set; } }
public sealed class CustomerRecord { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? DocumentNumber { get; set; } public string? Phone { get; set; } public string Currency { get; set; } = "USD"; public decimal CreditLimit { get; set; } public bool Active { get; set; } = true; public DateTime CreatedAtUtc { get; set; } }
public sealed class ReceivableEntryRecord { public Guid Id { get; set; } public Guid CustomerId { get; set; } public Guid? SaleId { get; set; } public string EntryType { get; set; } = "Charge"; public decimal Amount { get; set; } public string Currency { get; set; } = "USD"; public decimal? OriginalAmount { get; set; } public string? OriginalCurrency { get; set; } public decimal? AppliedExchangeRate { get; set; } public string Reason { get; set; } = ""; public Guid ActorId { get; set; } public Guid TerminalId { get; set; } public DateTime OccurredAtUtc { get; set; } }
public sealed class SaleReturnRecord { public Guid Id { get; set; } public Guid SaleId { get; set; } public Guid OperatorId { get; set; } public Guid TerminalId { get; set; } public Guid CashSessionId { get; set; } public decimal Amount { get; set; } public string Currency { get; set; } = "VES"; public string Reason { get; set; } = ""; public DateTime CreatedAtUtc { get; set; } }
public sealed class SaleReturnLineRecord { public Guid Id { get; set; } public Guid SaleReturnId { get; set; } public Guid SaleLineId { get; set; } public Guid ProductId { get; set; } public decimal Quantity { get; set; } public decimal UnitPrice { get; set; } public decimal Amount { get; set; } public bool Restock { get; set; } }
public sealed class SaleReturnPaymentRecord { public Guid Id { get; set; } public Guid SaleReturnId { get; set; } public Guid OriginalPaymentId { get; set; } public string Method { get; set; } = ""; public decimal Amount { get; set; } public string Currency { get; set; } = "VES"; public decimal? AppliedExchangeRate { get; set; } public string? Reference { get; set; } }
public sealed class ReturnIdempotencyRecord { public Guid Id { get; set; } public Guid TerminalId { get; set; } public string Key { get; set; } = ""; public string PayloadHash { get; set; } = ""; public Guid SaleReturnId { get; set; } public DateTime CreatedAtUtc { get; set; } }
public sealed class PairedTerminalRecord { public Guid TerminalId { get; set; } public string CredentialHash { get; set; } = ""; public Guid PairedByOperatorId { get; set; } public DateTime PairedAtUtc { get; set; } public bool Active { get; set; } = true; }
public sealed class TerminalClientVersionRecord { public Guid TerminalId { get; set; } public string? ClientVersion { get; set; } public DateTime LastReportedAtUtc { get; set; } }
