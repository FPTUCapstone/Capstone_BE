using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ServiceBookingConfiguration : IEntityTypeConfiguration<ServiceBooking>
{
    public void Configure(EntityTypeBuilder<ServiceBooking> builder)
    {
        builder.ToTable("ServiceBookings", "commercial");
        builder.HasKey(booking => booking.Id);
        builder.Property(booking => booking.Id).HasColumnName("service_booking_id").ValueGeneratedOnAdd();
        builder.Property(booking => booking.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(booking => booking.ServiceId).HasColumnName("service_id").IsRequired();
        builder.Property(booking => booking.StartAtUtc).HasColumnName("start_datetime").AsUtcDateTime2().IsRequired();
        builder.Property(booking => booking.EndAtUtc).HasColumnName("end_datetime").AsUtcDateTime2();
        builder.Property(booking => booking.Quantity).HasColumnName("quantity").IsRequired();
        builder.Property(booking => booking.TotalPrice).HasColumnName("total_price").HasPrecision(12, 2).IsRequired();
        builder.Property(booking => booking.CommissionAmount).HasColumnName("commission_amount").HasPrecision(12, 2);
        builder.Property(booking => booking.Status).HasColumnName("status").HasMaxLength(24).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(booking => booking.RequestSubmittedAtUtc).HasColumnName("request_submitted_at").AsUtcDateTime2();
        builder.Property(booking => booking.ActualAmount).HasColumnName("actual_amount").HasPrecision(12, 2);
        builder.Property(booking => booking.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(ServiceBooking.RejectionReasonMaxLength);
        builder.Property(booking => booking.RespondedByUserId).HasColumnName("responded_by_user_id");
        builder.Property(booking => booking.RespondedAtUtc).HasColumnName("responded_at").AsUtcDateTime2();
        builder.Property(booking => booking.OutcomeByUserId).HasColumnName("outcome_by_user_id");
        builder.Property(booking => booking.OutcomeAtUtc).HasColumnName("outcome_at").AsUtcDateTime2();
        builder.Property(booking => booking.IsAutoCompleted).HasColumnName("is_auto_completed").HasDefaultValue(false);
        builder.Property(booking => booking.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.Property(booking => booking.ItineraryId).HasColumnName("itinerary_id");
        builder.Property(booking => booking.ServiceOption).HasColumnName("service_option").HasMaxLength(ServiceBooking.ServiceOptionMaxLength);
        builder.Property(booking => booking.SpecialRequest).HasColumnName("special_request").HasMaxLength(ServiceBooking.SpecialRequestMaxLength);
        builder.Ignore(booking => booking.RequestedDate);
        builder.Ignore(booking => booking.ResponseDeadlineUtc);
        builder.Ignore(booking => booking.OutcomeAvailableAtUtc);
        builder.Ignore(booking => booking.AutoCompleteAtUtc);
        builder.HasOne<User>().WithMany().HasForeignKey(booking => booking.RespondedByUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(booking => booking.OutcomeByUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Itinerary>().WithMany().HasForeignKey(booking => booking.ItineraryId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(booking => booking.Service).WithMany().HasForeignKey(booking => booking.ServiceId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(booking => new { booking.ServiceId, booking.Status }).HasDatabaseName("IX_ServiceBookings_Service");
    }
}