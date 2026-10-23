using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutPeriodBreakdownConfiguration : IEntityTypeConfiguration<PayoutPeriodBreakdown>
{
    public void Configure(EntityTypeBuilder<PayoutPeriodBreakdown> builder)
    {
        builder.ToTable("PayoutPeriodBreakdowns", "payment");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("breakdown_id").ValueGeneratedOnAdd();
        builder.Property(b => b.PayoutId).HasColumnName("payout_id").IsRequired();
        builder.Property(b => b.SettlementMonth).HasColumnName("settlement_month").IsRequired();
        builder.Property(b => b.EligibleBookingCount).HasColumnName("eligible_booking_count").IsRequired();
        builder.Property(b => b.PaymentsReceived).HasColumnName("payments_received").HasPrecision(14, 2).IsRequired();
        builder.Property(b => b.DiscountsGiven).HasColumnName("discounts_given").HasPrecision(14, 2).IsRequired();
        builder.Property(b => b.RefundedAmount).HasColumnName("refunded_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(b => b.CommissionAmount).HasColumnName("commission_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(b => b.PayableNetAmount).HasColumnName("payable_net_amount").HasPrecision(14, 2).IsRequired();

        builder.HasIndex(b => new { b.PayoutId, b.SettlementMonth })
            .IsUnique()
            .HasDatabaseName("UQ_PayoutPeriodBreakdowns");
    }
}