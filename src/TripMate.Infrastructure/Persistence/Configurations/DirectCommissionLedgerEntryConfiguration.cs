using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectCommissionLedgerEntryConfiguration : IEntityTypeConfiguration<DirectCommissionLedgerEntry>
{
    public void Configure(EntityTypeBuilder<DirectCommissionLedgerEntry> b)
    {
        b.ToTable("TourCommissionLedgerEntries", "commercial");
        b.HasKey(e => e.Id); b.Property(e => e.Id).HasColumnName("entry_id").ValueGeneratedOnAdd();
        b.Property(e => e.StatementId).HasColumnName("statement_id");
        b.Property(e => e.BookingId).HasColumnName("booking_id");
        b.Property(e => e.SourceType).HasColumnName("source_type").HasMaxLength(DirectCommissionLedgerEntry.SourceTypeMaxLength).IsUnicode(false);
        b.Property(e => e.SourceId).HasColumnName("source_id");
        b.Property(e => e.PaymentTransactionId).HasColumnName("payment_transaction_id");
        b.Property(e => e.BaseAmountVnd).HasColumnName("base_amount_vnd");
        b.Property(e => e.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 4);
        b.Property(e => e.CommissionAmountVnd).HasColumnName("commission_amount_vnd");
        b.Property(e => e.SourceOccurredAtUtc).HasColumnName("source_occurred_at").AsUtcDateTime2();
        b.Property(e => e.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasOne(e => e.Statement).WithMany().HasForeignKey(e => e.StatementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Booking>().WithMany().HasForeignKey(e => e.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentTransaction>().WithMany().HasForeignKey(e => e.PaymentTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.SourceType, e.SourceId }).IsUnique().HasDatabaseName("UX_TourCommissionLedgerEntries_Source");
        b.HasIndex(e => new { e.BookingId, e.SourceType }).HasDatabaseName("IX_TourCommissionLedgerEntries_Booking");
    }
}