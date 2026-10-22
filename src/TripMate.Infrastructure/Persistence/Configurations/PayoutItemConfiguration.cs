using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutItemConfiguration : IEntityTypeConfiguration<PayoutItem>
{
    public void Configure(EntityTypeBuilder<PayoutItem> builder)
    {
        builder.ToTable("PayoutItems", "payment", table =>
            table.HasCheckConstraint("CK_PayoutItems_Amount", "paid_amount >= 0"));
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("payout_item_id").ValueGeneratedOnAdd();
        builder.Property(item => item.PayoutId).HasColumnName("payout_id").IsRequired();
        builder.Property(item => item.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(item => item.PaymentTransactionId).HasColumnName("payment_transaction_id");
        // Original schema column name is "amount"; this migration renames its meaning to "the
        // paid amount captured for this payout item" without adding a second, redundant column.
        builder.Property(item => item.PaidAmount).HasColumnName("paid_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(item => item.DiscountAmount).HasColumnName("discount_amount").HasPrecision(12, 2).HasDefaultValue(0m).IsRequired();
        builder.Property(item => item.CommissionRateApplied).HasColumnName("commission_rate_applied").HasPrecision(5, 2);
        builder.Property(item => item.CommissionAmount).HasColumnName("commission_amount").HasPrecision(12, 2).HasDefaultValue(0m).IsRequired();

        builder.HasOne<Booking>().WithMany().HasForeignKey(item => item.BookingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.PayoutId, item.BookingId })
            .IsUnique()
            .HasDatabaseName("UQ_PayoutItems");
    }
}