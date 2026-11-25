using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps commerce.TourPendingChangeSets from
/// database/migrations/20261125_tour_pending_change_sets.sql (TM-82 §8).
/// </summary>
public class TourPendingChangeSetConfiguration : IEntityTypeConfiguration<TourPendingChangeSet>
{
    public void Configure(EntityTypeBuilder<TourPendingChangeSet> builder)
    {
        builder.ToTable("TourPendingChangeSets", "commerce");

        builder.HasKey(changeSet => changeSet.Id);
        builder.Property(changeSet => changeSet.Id)
            .HasColumnName("tour_pending_change_set_id")
            .ValueGeneratedOnAdd();

        builder.Property(changeSet => changeSet.TourId)
            .HasColumnName("tour_id")
            .IsRequired();
        builder.Property(changeSet => changeSet.SubmittedByUserId)
            .HasColumnName("submitted_by_user_id")
            .IsRequired();
        builder.Property(changeSet => changeSet.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(13)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(changeSet => changeSet.CategoryId)
            .HasColumnName("category_id");
        builder.Property(changeSet => changeSet.Title)
            .HasColumnName("title")
            .HasMaxLength(Tour.TitleMaxLength)
            .IsRequired();
        builder.Property(changeSet => changeSet.Description)
            .HasColumnName("description")
            .IsRequired();
        builder.Property(changeSet => changeSet.BasePrice)
            .HasColumnName("base_price")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(changeSet => changeSet.DurationDays)
            .HasColumnName("duration_days")
            .IsRequired();
        builder.Property(changeSet => changeSet.Inclusions)
            .HasColumnName("inclusions");
        builder.Property(changeSet => changeSet.Exclusions)
            .HasColumnName("exclusions");
        builder.Property(changeSet => changeSet.Highlights)
            .HasColumnName("highlights");
        builder.Property(changeSet => changeSet.TravellerRequirements)
            .HasColumnName("traveller_requirements");
        builder.Property(changeSet => changeSet.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(Tour.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(changeSet => changeSet.DestinationSummary)
            .HasColumnName("destination_summary")
            .HasMaxLength(Tour.DestinationSummaryMaxLength)
            .IsRequired();
        builder.Property(changeSet => changeSet.CancellationPolicyTemplateId)
            .HasColumnName("cancellation_policy_template_id")
            .IsRequired();
        builder.Property(changeSet => changeSet.DestinationIdsJson)
            .HasColumnName("destination_ids_json")
            .IsRequired();
        builder.Property(changeSet => changeSet.ItineraryItemsJson)
            .HasColumnName("itinerary_items_json")
            .IsRequired();
        builder.Property(changeSet => changeSet.SchedulesJson)
            .HasColumnName("schedules_json")
            .IsRequired();
        builder.Property(changeSet => changeSet.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(changeSet => changeSet.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(changeSet => changeSet.SubmittedAtUtc)
            .HasColumnName("submitted_at")
            .AsUtcDateTime2();
        builder.Property(changeSet => changeSet.ReviewedByUserId)
            .HasColumnName("reviewed_by_user_id");
        builder.Property(changeSet => changeSet.ReviewedAtUtc)
            .HasColumnName("reviewed_at")
            .AsUtcDateTime2();
        builder.Property(changeSet => changeSet.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasMaxLength(Tour.RejectionReasonMaxLength);

        builder.HasOne(changeSet => changeSet.Tour)
            .WithMany()
            .HasForeignKey(changeSet => changeSet.TourId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(changeSet => changeSet.TourId)
            .HasDatabaseName("IX_TourPendingChangeSets_Tour");
    }
}