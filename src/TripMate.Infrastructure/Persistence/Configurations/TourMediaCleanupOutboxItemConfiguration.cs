using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourMediaCleanupOutboxItemConfiguration
    : IEntityTypeConfiguration<TourMediaCleanupOutboxItem>
{
    public void Configure(EntityTypeBuilder<TourMediaCleanupOutboxItem> builder)
    {
        builder.ToTable(
            "TourMediaCleanupOutbox",
            "commerce",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_TourMediaCleanupOutbox_Status",
                    "cleanup_status IN ('Pending','InProgress','Completed','Exhausted')");
                table.HasCheckConstraint(
                    "CK_TourMediaCleanupOutbox_Attempts",
                    "max_attempts BETWEEN 1 AND 100 AND attempt_count BETWEEN 0 AND max_attempts");
                table.HasCheckConstraint(
                    "CK_TourMediaCleanupOutbox_State",
                    "(cleanup_status = 'Pending' AND lease_token IS NULL " +
                    "AND lease_expires_at IS NULL AND completed_at IS NULL) OR " +
                    "(cleanup_status = 'InProgress' AND lease_token IS NOT NULL " +
                    "AND lease_expires_at IS NOT NULL AND completed_at IS NULL " +
                    "AND attempt_count BETWEEN 1 AND max_attempts) OR " +
                    "(cleanup_status = 'Completed' AND lease_token IS NULL " +
                    "AND lease_expires_at IS NULL AND completed_at IS NOT NULL) OR " +
                    "(cleanup_status = 'Exhausted' AND lease_token IS NULL " +
                    "AND lease_expires_at IS NULL AND completed_at IS NOT NULL " +
                    "AND attempt_count BETWEEN 1 AND max_attempts)");
            });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id)
            .HasColumnName("cleanup_outbox_id")
            .ValueGeneratedOnAdd();
        builder.Property(item => item.TourMediaId)
            .HasColumnName("tour_media_id");
        builder.Property(item => item.CloudinaryPublicId)
            .HasColumnName("cloudinary_public_id")
            .HasMaxLength(TourMediaCleanupOutboxItem.CloudinaryPublicIdMaxLength)
            .IsRequired();
        builder.Property(item => item.Status)
            .HasColumnName("cleanup_status")
            .HasConversion<string>()
            .HasMaxLength(TourMediaCleanupOutboxItem.StatusMaxLength)
            .IsUnicode(false)
            .HasDefaultValueSql("'Pending'")
            .IsRequired();
        builder.Property(item => item.NotBeforeAtUtc)
            .HasColumnName("not_before_at")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(item => item.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(item => item.MaxAttempts)
            .HasColumnName("max_attempts")
            .HasDefaultValue(TourMediaCleanupOutboxItem.DefaultMaxAttempts)
            .IsRequired();
        builder.Property(item => item.LeaseToken)
            .HasColumnName("lease_token");
        builder.Property(item => item.LeaseExpiresAtUtc)
            .HasColumnName("lease_expires_at")
            .AsUtcDateTime2();
        builder.Property(item => item.LastErrorCode)
            .HasColumnName("last_error_code")
            .HasMaxLength(TourMediaCleanupOutboxItem.LastErrorCodeMaxLength);
        builder.Property(item => item.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(item => item.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(item => item.CompletedAtUtc)
            .HasColumnName("completed_at")
            .AsUtcDateTime2();

        builder.HasOne(item => item.TourMedia)
            .WithMany()
            .HasForeignKey(item => item.TourMediaId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(item => item.CloudinaryPublicId)
            .HasDatabaseName("UX_TourMediaCleanupOutbox_PublicId")
            .IsUnique();
        builder.HasIndex(item => item.TourMediaId)
            .HasDatabaseName("UX_TourMediaCleanupOutbox_Media")
            .HasFilter("[tour_media_id] IS NOT NULL")
            .IsUnique();
        builder.HasIndex(item => new
        {
            item.Status,
            item.NotBeforeAtUtc,
            item.LeaseExpiresAtUtc,
            item.Id,
        })
            .HasDatabaseName("IX_TourMediaCleanupOutbox_Due");
    }
}