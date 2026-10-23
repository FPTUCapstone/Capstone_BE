using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps commerce.Bookings from database/tripmate_schema_v7.sql. The table has no subtotal
/// column; callers derive it from TotalAmount + DiscountAmount via Booking.SubtotalAmount.
/// </summary>
public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("Bookings", "commerce");

        builder.HasKey(booking => booking.Id);
        builder.Property(booking => booking.Id)
            .HasColumnName("booking_id")
            .ValueGeneratedOnAdd();
        builder.Property(booking => booking.BookingCode)
            .HasColumnName("booking_code")
            .HasMaxLength(Booking.BookingCodeMaxLength)
            .IsRequired();
        builder.Property(booking => booking.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(booking => booking.TourScheduleId)
            .HasColumnName("tour_schedule_id")
            .IsRequired();
        builder.Property(booking => booking.VoucherId)
            .HasColumnName("voucher_id");
        builder.Property(booking => booking.Quantity)
            .HasColumnName("quantity")
            .IsRequired();
        builder.Property(booking => booking.UnitPrice)
            .HasColumnName("unit_price")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(booking => booking.DiscountAmount)
            .HasColumnName("discount_amount")
            .HasPrecision(12, 2)
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(booking => booking.TotalAmount)
            .HasColumnName("total_amount")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(booking => booking.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(14)
            .IsUnicode(false)
            .HasDefaultValueSql("'PendingPayment'")
            .IsRequired();
        builder.Property(booking => booking.PaymentStatus)
            .HasColumnName("payment_status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsUnicode(false)
            .HasDefaultValueSql("'Unpaid'")
            .IsRequired();
        builder.Property(booking => booking.BookedAtUtc)
            .HasColumnName("booked_at")
            .AsUtcDateTime2()
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();
        builder.Property(booking => booking.PaymentExpiresAtUtc)
            .HasColumnName("payment_expires_at")
            .AsUtcDateTime2();
        builder.Property(booking => booking.CancelledAtUtc)
            .HasColumnName("cancelled_at")
            .AsUtcDateTime2();
        builder.Property(booking => booking.CancelReason)
            .HasColumnName("cancel_reason")
            .HasMaxLength(Booking.CancelReasonMaxLength);
        builder.Property(booking => booking.CancellationPolicySnapshot)
            .HasColumnName("cancellation_policy_snapshot")
            .HasMaxLength(Booking.PolicySnapshotMaxLength)
            .HasDefaultValue(Booking.DefaultCancellationPolicySnapshot)
            .IsRequired();

        builder.Ignore(booking => booking.SubtotalAmount);

        builder.HasOne<User>().WithMany().HasForeignKey(booking => booking.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(booking => booking.TourSchedule).WithMany()
            .HasForeignKey(booking => booking.TourScheduleId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Voucher>().WithMany().HasForeignKey(booking => booking.VoucherId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(booking => booking.Participants)
            .WithOne(participant => participant.Booking)
            .HasForeignKey(participant => participant.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(booking => booking.Tickets)
            .WithOne()
            .HasForeignKey(ticket => ticket.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(booking => booking.BookingCode).IsUnique().HasDatabaseName("UX_Bookings_BookingCode");
        builder.HasIndex(booking => new { booking.TravelerUserId, booking.Status })
            .HasDatabaseName("IX_Bookings_Traveler");
        builder.HasIndex(booking => new { booking.TourScheduleId, booking.Status })
            .HasDatabaseName("IX_Bookings_TourSchedule");
    }
}