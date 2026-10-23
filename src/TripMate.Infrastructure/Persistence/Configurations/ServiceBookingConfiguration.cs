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
        builder.Property(booking => booking.Status).HasColumnName("status").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(booking => booking.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasOne(booking => booking.Service).WithMany().HasForeignKey(booking => booking.ServiceId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(booking => new { booking.ServiceId, booking.Status }).HasDatabaseName("IX_ServiceBookings_Service");
    }
}