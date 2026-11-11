using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutSettlementConfiguration : IEntityTypeConfiguration<PayoutSettlement>
{
    public void Configure(EntityTypeBuilder<PayoutSettlement> builder)
    {
        builder.ToTable("PayoutSettlement", "payment");
        builder.HasKey(settlement => settlement.Id);
        builder.Property(settlement => settlement.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(settlement => settlement.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(settlement => settlement.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(settlement => settlement.TourOperatorUserId).HasColumnName("tour_operator_user_id").IsRequired();
        builder.Property(settlement => settlement.RunDate).HasColumnName("run_date").IsRequired();
        builder.Property(settlement => settlement.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        builder.Property(settlement => settlement.GrossAmount).HasColumnName("gross_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(settlement => settlement.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 2).IsRequired();
        builder.Property(settlement => settlement.CommissionAmount).HasColumnName("commission_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(settlement => settlement.NetAmount).HasColumnName("net_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(settlement => settlement.DebtOffsetAmount).HasColumnName("debt_offset_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(settlement => settlement.PayoutAmount).HasColumnName("payout_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(settlement => settlement.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
        builder.Property(settlement => settlement.TransferReference).HasColumnName("transfer_reference").HasMaxLength(100);
        builder.Property(settlement => settlement.CreatedAtUtc).HasColumnName("created_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(settlement => settlement.UpdatedAtUtc).HasColumnName("updated_at_utc").AsUtcDateTime2().IsRequired();

        builder.HasOne<Booking>().WithMany().HasForeignKey(settlement => settlement.BookingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(settlement => settlement.TourOperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(settlement => new { settlement.BookingId, settlement.Kind })
            .IsUnique().HasDatabaseName("UX_PayoutSettlement_Booking_Kind");
        builder.HasIndex(settlement => new { settlement.TourOperatorUserId, settlement.RunDate })
            .HasDatabaseName("IX_PayoutSettlement_Operator_Run");
        builder.HasIndex(settlement => settlement.Status).HasDatabaseName("IX_PayoutSettlement_Status");
    }
}