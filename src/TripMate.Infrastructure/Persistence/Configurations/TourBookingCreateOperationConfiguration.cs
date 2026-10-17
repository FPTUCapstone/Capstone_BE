using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>Maps commerce.TourBookingCreateOperations, added by the UC-27 idempotency migration.</summary>
public sealed class TourBookingCreateOperationConfiguration : IEntityTypeConfiguration<TourBookingCreateOperation>
{
    public void Configure(EntityTypeBuilder<TourBookingCreateOperation> builder)
    {
        builder.ToTable("TourBookingCreateOperations", "commerce");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id)
            .HasColumnName("create_operation_id")
            .ValueGeneratedOnAdd();
        builder.Property(operation => operation.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(operation => operation.PayloadFingerprint)
            .HasColumnName("payload_fingerprint")
            .HasMaxLength(TourBookingCreateOperation.PayloadFingerprintLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.BookingId).HasColumnName("booking_id");
        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(operation => operation.CompletedAtUtc)
            .HasColumnName("completed_at").AsUtcDateTime2();
        builder.HasOne<User>().WithMany().HasForeignKey(operation => operation.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(operation => operation.Booking).WithMany().HasForeignKey(operation => operation.BookingId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(operation => new { operation.TravelerUserId, operation.IdempotencyKey })
            .HasDatabaseName("UQ_TourBookingCreateOperations_TravelerKey").IsUnique();
    }
}
