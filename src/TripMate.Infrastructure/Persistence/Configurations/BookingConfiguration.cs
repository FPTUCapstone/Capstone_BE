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
        builder.Property(booking => booking.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(booking => booking.TourScheduleId).HasColumnName("tour_schedule_id");
        builder.Property(booking => booking.Quantity).HasColumnName("quantity").IsRequired();
        builder.Property(booking => booking.TotalAmount).HasColumnName("total_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(booking => booking.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(14).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.PaymentStatus).HasColumnName("payment_status").HasConversion<string>().HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.CancelledAtUtc).HasColumnName("cancelled_at").AsUtcDateTime2();
        builder.Property(booking => booking.CancelReason).HasColumnName("cancel_reason").HasMaxLength(Booking.CancelReasonMaxLength);
        builder.Property(booking => booking.CancellationPolicySnapshot).HasColumnName("cancellation_policy_snapshot").HasMaxLength(Booking.PolicySnapshotMaxLength).IsRequired();
        builder.HasOne(booking => booking.TourSchedule).WithMany().HasForeignKey(booking => booking.TourScheduleId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(booking => booking.Tickets).WithOne().HasForeignKey(ticket => ticket.BookingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(booking => new { booking.TravelerUserId, booking.Status }).HasDatabaseName("IX_Bookings_Traveler");
        builder.HasIndex(booking => new { booking.TourScheduleId, booking.Status }).HasDatabaseName("IX_Bookings_TourSchedule");
    }
}
