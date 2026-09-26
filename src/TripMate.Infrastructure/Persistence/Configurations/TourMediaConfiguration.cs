using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourMediaConfiguration : IEntityTypeConfiguration<TourMedia>
{
    public void Configure(EntityTypeBuilder<TourMedia> builder)
    {
        builder.ToTable(
            "TourMedia",
            "commerce",
            table =>
            {
                table.HasCheckConstraint("CK_TourMedia_SortOrderPositive", "sort_order > 0");
                table.HasCheckConstraint(
                    "CK_TourMedia_Lifecycle",
                    "lifecycle_status IN ('Active','Deleted')");
                table.HasCheckConstraint(
                    "CK_TourMedia_DeletedAt",
                    "(lifecycle_status = 'Active' AND deleted_at IS NULL) OR " +
                    "(lifecycle_status = 'Deleted' AND deleted_at IS NOT NULL)");
            });

        builder.HasKey(media => media.Id);
        builder.Property(media => media.Id)
            .HasColumnName("tour_media_id")
            .ValueGeneratedOnAdd();
        builder.Property(media => media.TourId)
            .HasColumnName("tour_id")
            .IsRequired();
        builder.Property(media => media.CloudinaryPublicId)
            .HasColumnName("cloudinary_public_id")
            .HasMaxLength(TourMedia.CloudinaryPublicIdMaxLength)
            .IsRequired();
        builder.Property(media => media.DeliveryUrl)
            .HasColumnName("delivery_url")
            .HasMaxLength(TourMedia.DeliveryUrlMaxLength)
            .IsRequired();
        builder.Property(media => media.Caption)
            .HasColumnName("caption")
            .HasMaxLength(TourMedia.CaptionMaxLength);
        builder.Property(media => media.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired();
        builder.Property(media => media.IsPrimary)
            .HasColumnName("is_primary")
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(media => media.LifecycleStatus)
            .HasColumnName("lifecycle_status")
            .HasConversion<string>()
            .HasMaxLength(TourMedia.LifecycleStatusMaxLength)
            .IsUnicode(false)
            .HasDefaultValueSql("'Active'")
            .IsRequired();
        builder.Property(media => media.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(media => media.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(media => media.DeletedAtUtc)
            .HasColumnName("deleted_at")
            .AsUtcDateTime2();
        builder.Property(media => media.AltText)
            .HasColumnName("alt_text")
            .HasMaxLength(TourMedia.AltTextMaxLength)
            .UseCollation(TourMedia.AltTextCollation)
            .IsRequired();

        builder.HasOne(media => media.Tour)
            .WithMany(tour => tour.Media)
            .HasForeignKey(media => media.TourId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(media => new { media.TourId, media.SortOrder })
            .HasDatabaseName("UX_TourMedia_ActiveSortOrder")
            .HasFilter("[lifecycle_status] = 'Active'")
            .IsUnique();
        builder.HasIndex(media => media.TourId)
            .HasDatabaseName("UX_TourMedia_ActivePrimary")
            .HasFilter("[lifecycle_status] = 'Active' AND [is_primary] = 1")
            .IsUnique();
        builder.HasIndex(media => new
        {
            media.TourId,
            media.LifecycleStatus,
            media.SortOrder,
            media.Id,
        })
            .HasDatabaseName("IX_TourMedia_TourLifecycleOrder");
        builder.HasIndex(media => media.CloudinaryPublicId)
            .HasDatabaseName("UX_TourMedia_CloudinaryPublicId")
            .IsUnique();

        builder.HasQueryFilter(media =>
            media.LifecycleStatus == TourMediaLifecycleStatus.Active);
    }
}