using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>Maps commerce.VoucherRedemptions from database/tripmate_schema_v7.sql.</summary>
public sealed class VoucherRedemptionConfiguration : IEntityTypeConfiguration<VoucherRedemption>
{
    public void Configure(EntityTypeBuilder<VoucherRedemption> builder)
    {
        builder.ToTable("VoucherRedemptions", "commerce");

        builder.HasKey(redemption => redemption.Id);
        builder.Property(redemption => redemption.Id)
            .HasColumnName("redemption_id")
            .ValueGeneratedOnAdd();
        builder.Property(redemption => redemption.VoucherId)
            .HasColumnName("voucher_id")
            .IsRequired();
        builder.Property(redemption => redemption.BookingId)
            .HasColumnName("booking_id")
            .IsRequired();
        builder.Property(redemption => redemption.DiscountAmount)
            .HasColumnName("discount_amount")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(redemption => redemption.RedeemedAtUtc)
            .HasColumnName("redeemed_at")
            .AsUtcDateTime2()
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.HasOne(redemption => redemption.Voucher).WithMany()
            .HasForeignKey(redemption => redemption.VoucherId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(redemption => redemption.Booking).WithMany()
            .HasForeignKey(redemption => redemption.BookingId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(redemption => redemption.BookingId).IsUnique()
            .HasDatabaseName("UX_VoucherRedemptions_Booking");
    }
}
