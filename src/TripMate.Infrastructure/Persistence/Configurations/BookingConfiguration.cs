using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("Bookings", "commerce");
        builder.HasKey(booking => booking.Id);
        builder.Property(booking => booking.Id).HasColumnName("booking_id").ValueGeneratedOnAdd();
        builder.Property(booking => booking.BookingCode).HasColumnName("booking_code").HasMaxLength(30).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(booking => booking.TourScheduleId).HasColumnName("tour_schedule_id");
        builder.Property(booking => booking.Quantity).HasColumnName("quantity").IsRequired();
        builder.Property(booking => booking.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 2).IsRequired();
        builder.Property(booking => booking.DiscountAmount).HasColumnName("discount_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(booking => booking.TotalAmount).HasColumnName("total_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(booking => booking.Status).HasColumnName("status").HasMaxLength(14).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.PaymentStatus).HasColumnName("payment_status").HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.BookedAtUtc).HasColumnName("booked_at").AsUtcDateTime2().IsRequired();
        builder.Property(booking => booking.CancelledAtUtc).HasColumnName("cancelled_at").AsUtcDateTime2();
        builder.Property(booking => booking.CancelReason).HasColumnName("cancel_reason").HasMaxLength(300);
        builder.HasOne(booking => booking.TourSchedule).WithMany()
            .HasForeignKey(booking => booking.TourScheduleId).OnDelete(DeleteBehavior.Restrict);
    }
}