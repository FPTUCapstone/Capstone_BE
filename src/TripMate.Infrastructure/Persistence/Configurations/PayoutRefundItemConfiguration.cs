using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutRefundItemConfiguration : IEntityTypeConfiguration<PayoutRefundItem>
{
    public void Configure(EntityTypeBuilder<PayoutRefundItem> builder)
    {
        builder.ToTable("PayoutRefundItems", "payment");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("payout_refund_item_id").ValueGeneratedOnAdd();
        builder.Property(item => item.PayoutId).HasColumnName("payout_id").IsRequired();
        builder.Property(item => item.RefundId).HasColumnName("refund_id").IsRequired();
        builder.Property(item => item.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(item => item.RefundedAmount).HasColumnName("refunded_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(item => item.CommissionRateApplied).HasColumnName("commission_rate_applied").HasPrecision(5, 2).IsRequired();
        builder.Property(item => item.CommissionReduction).HasColumnName("commission_reduction").HasPrecision(12, 2).IsRequired();

        builder.HasOne<Refund>().WithMany().HasForeignKey(item => item.RefundId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Booking>().WithMany().HasForeignKey(item => item.BookingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.PayoutId, item.RefundId })
            .IsUnique()
            .HasDatabaseName("UQ_PayoutRefundItems");
    }
}