using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps commerce.Tours from database/tripmate_schema_v7.sql.
/// </summary>
public class TourConfiguration : IEntityTypeConfiguration<Tour>
{
    public void Configure(EntityTypeBuilder<Tour> builder)
    {
        builder.ToTable("Tours", "commerce");

        builder.HasKey(tour => tour.Id);
        builder.Property(tour => tour.Id)
            .HasColumnName("tour_id")
            .ValueGeneratedOnAdd();

        builder.Property(tour => tour.OperatorUserId)
            .HasColumnName("operator_user_id")
            .IsRequired();
        builder.Property(tour => tour.VersionRootTourId)
            .HasColumnName("version_root_tour_id");
        builder.Property(tour => tour.VersionNo)
            .HasColumnName("version_no")
            .HasDefaultValue(1)
            .IsRequired();
        builder.Property(tour => tour.CategoryId)
            .HasColumnName("category_id");
        builder.Property(tour => tour.Title)
            .HasColumnName("title")
            .HasMaxLength(Tour.TitleMaxLength)
            .IsRequired();
        builder.Property(tour => tour.DestinationSummary)
            .HasColumnName("destination_summary")
            .HasMaxLength(Tour.DestinationSummaryMaxLength)
            .IsRequired();
        builder.Property(tour => tour.Description)
            .HasColumnName("description");
        builder.Property(tour => tour.BasePrice)
            .HasColumnName("base_price")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(tour => tour.DurationDays)
            .HasColumnName("duration_days")
            .HasDefaultValue(1)
            .IsRequired();
        builder.Property(tour => tour.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(Tour.TimeZoneIdMaxLength);
        builder.Property(tour => tour.Highlights)
            .HasColumnName("highlights");
        builder.Property(tour => tour.Inclusions)
            .HasColumnName("inclusions");
        builder.Property(tour => tour.Exclusions)
            .HasColumnName("exclusions");
        builder.Property(tour => tour.TravellerRequirements)
            .HasColumnName("traveller_requirements");
        builder.Property(tour => tour.CancellationPolicyTemplateId)
            .HasColumnName("cancellation_policy_template_id");
        builder.Property(tour => tour.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(12)
            .IsUnicode(false)
            .HasDefaultValueSql("'Draft'")
            .IsRequired();
        builder.Property(tour => tour.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasMaxLength(Tour.RejectionReasonMaxLength);
        builder.Property(tour => tour.ReviewedBy)
            .HasColumnName("reviewed_by");
        builder.Property(tour => tour.ReviewedAtUtc)
            .HasColumnName("reviewed_at")
            .AsUtcDateTime2();
        builder.Property(tour => tour.PublishedAtUtc)
            .HasColumnName("published_at")
            .AsUtcDateTime2();
        builder.Property(tour => tour.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(tour => tour.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(tour => tour.OperatorProfile)
            .WithMany()
            .HasForeignKey(tour => tour.OperatorUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(tour => tour.Reviewer)
            .WithMany()
            .HasForeignKey(tour => tour.ReviewedBy)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(tour => tour.Category)
            .WithMany(category => category.Tours)
            .HasForeignKey(tour => tour.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(tour => tour.Schedules)
            .WithOne(schedule => schedule.Tour)
            .HasForeignKey(schedule => schedule.TourId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(tour => tour.Destinations)
            .WithOne(link => link.Tour)
            .HasForeignKey(link => link.TourId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(tour => tour.ItineraryItems)
            .WithOne(item => item.Tour)
            .HasForeignKey(item => item.TourId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(tour => tour.CancellationPolicyRules)
            .WithOne(rule => rule.Tour)
            .HasForeignKey(rule => rule.TourId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(tour => tour.Media)
            .WithOne(media => media.Tour)
            .HasForeignKey(media => media.TourId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(tour => tour.MediaUploadOperations)
            .WithOne(operation => operation.Tour)
            .HasForeignKey(operation => operation.TourId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(tour => tour.Schedules)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(tour => tour.Destinations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(tour => tour.ItineraryItems)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(tour => tour.CancellationPolicyRules)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(tour => tour.Media)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(tour => tour.MediaUploadOperations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(tour => tour.OperatorUserId)
            .HasDatabaseName("IX_Tours_Operator");
        builder.HasIndex(tour => new { tour.VersionRootTourId, tour.VersionNo })
            .IsUnique()
            .HasDatabaseName("UX_Tours_VersionLineage");
        builder.HasIndex(tour => tour.Status)
            .HasDatabaseName("IX_Tours_Status");
        builder.HasIndex(tour => tour.CategoryId)
            .HasDatabaseName("IX_Tours_Category");
    }
}