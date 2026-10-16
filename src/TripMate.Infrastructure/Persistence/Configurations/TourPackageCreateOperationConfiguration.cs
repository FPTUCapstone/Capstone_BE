using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourPackageCreateOperationConfiguration : IEntityTypeConfiguration<TourPackageCreateOperation>
{
    public void Configure(EntityTypeBuilder<TourPackageCreateOperation> builder)
    {
        builder.ToTable("TourPackageCreateOperations", "commerce");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id)
            .HasColumnName("create_operation_id")
            .ValueGeneratedOnAdd();
        builder.Property(operation => operation.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(operation => operation.PayloadFingerprint)
            .HasColumnName("payload_fingerprint")
            .HasMaxLength(TourPackageCreateOperation.PayloadFingerprintLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.TourId).HasColumnName("tour_id");
        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(operation => operation.CompletedAtUtc)
            .HasColumnName("completed_at").AsUtcDateTime2();
        builder.HasOne<User>().WithMany().HasForeignKey(operation => operation.ActorUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(operation => operation.Tour).WithMany().HasForeignKey(operation => operation.TourId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(operation => new { operation.ActorUserId, operation.IdempotencyKey })
            .HasDatabaseName("UQ_TourPackageCreateOperations_ActorKey").IsUnique();
    }
}