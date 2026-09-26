using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourMediaUploadOperationConfiguration
    : IEntityTypeConfiguration<TourMediaUploadOperation>
{
    public void Configure(EntityTypeBuilder<TourMediaUploadOperation> builder)
    {
        builder.ToTable(
            "TourMediaUploadOperations",
            "commerce",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_TourMediaUploadOperations_Fingerprint",
                    "LEN(payload_fingerprint) = 64 AND " +
                    "payload_fingerprint NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2");
                table.HasCheckConstraint(
                    "CK_TourMediaUploadOperations_Status",
                    "operation_status IN ('Pending','Uploaded','Completed')");
                table.HasCheckConstraint(
                    "CK_TourMediaUploadOperations_State",
                    "(operation_status = 'Pending' AND provider_uploaded_at IS NULL " +
                    "AND tour_media_id IS NULL AND completed_at IS NULL) OR " +
                    "(operation_status = 'Uploaded' AND provider_uploaded_at IS NOT NULL " +
                    "AND tour_media_id IS NULL AND completed_at IS NULL) OR " +
                    "(operation_status = 'Completed' AND provider_uploaded_at IS NOT NULL " +
                    "AND tour_media_id IS NOT NULL AND completed_at IS NOT NULL)");
            });

        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id)
            .HasColumnName("upload_operation_id")
            .ValueGeneratedOnAdd();
        builder.Property(operation => operation.ActorUserId)
            .HasColumnName("actor_user_id")
            .IsRequired();
        builder.Property(operation => operation.TourId)
            .HasColumnName("tour_id")
            .IsRequired();
        builder.Property(operation => operation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();
        builder.Property(operation => operation.PayloadFingerprint)
            .HasColumnName("payload_fingerprint")
            .HasColumnType("char(64)")
            .UseCollation(TourMediaUploadOperation.FingerprintCollation)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.CloudinaryPublicId)
            .HasColumnName("cloudinary_public_id")
            .HasMaxLength(TourMediaUploadOperation.CloudinaryPublicIdMaxLength)
            .IsRequired();
        builder.Property(operation => operation.Status)
            .HasColumnName("operation_status")
            .HasConversion<string>()
            .HasMaxLength(TourMediaUploadOperation.StatusMaxLength)
            .IsUnicode(false)
            .HasDefaultValueSql("'Pending'")
            .IsRequired();
        builder.Property(operation => operation.TourMediaId)
            .HasColumnName("tour_media_id");
        builder.Property(operation => operation.ProviderUploadedAtUtc)
            .HasColumnName("provider_uploaded_at")
            .AsUtcDateTime2();
        builder.Property(operation => operation.CompletedAtUtc)
            .HasColumnName("completed_at")
            .AsUtcDateTime2();
        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(operation => operation.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(operation => operation.ActorUser)
            .WithMany()
            .HasForeignKey(operation => operation.ActorUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(operation => operation.Tour)
            .WithMany(tour => tour.MediaUploadOperations)
            .HasForeignKey(operation => operation.TourId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(operation => new
        {
            operation.ActorUserId,
            operation.TourId,
            operation.IdempotencyKey,
        })
            .HasDatabaseName("UX_TourMediaUploadOperations_ActorTourKey")
            .IsUnique();
        builder.HasIndex(operation => operation.CloudinaryPublicId)
            .HasDatabaseName("UX_TourMediaUploadOperations_PublicId")
            .IsUnique();
        builder.HasIndex(operation => new
        {
            operation.TourId,
            operation.Status,
            operation.Id,
        })
            .HasDatabaseName("IX_TourMediaUploadOperations_TourStatus");
    }
}