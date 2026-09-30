using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class RecommendationBehaviorEventConfiguration
    : IEntityTypeConfiguration<RecommendationBehaviorEvent>
{
    public void Configure(EntityTypeBuilder<RecommendationBehaviorEvent> builder)
    {
        builder.ToTable("RecommendationBehaviorEvents", "social");

        builder.HasKey(behaviorEvent => behaviorEvent.Id);
        builder.Property(behaviorEvent => behaviorEvent.Id)
            .HasColumnName("event_id")
            .ValueGeneratedOnAdd();
        builder.Property(behaviorEvent => behaviorEvent.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(behaviorEvent => behaviorEvent.PointOfInterestId)
            .HasColumnName("poi_id")
            .IsRequired();
        builder.Property(behaviorEvent => behaviorEvent.ItineraryId)
            .HasColumnName("itinerary_id");
        builder.Property(behaviorEvent => behaviorEvent.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(behaviorEvent => behaviorEvent.OriginalPosition)
            .HasColumnName("original_position");
        builder.Property(behaviorEvent => behaviorEvent.NewPosition)
            .HasColumnName("new_position");
        builder.Property(behaviorEvent => behaviorEvent.WasMandatory)
            .HasColumnName("was_mandatory");
        builder.Property(behaviorEvent => behaviorEvent.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(behaviorEvent => behaviorEvent.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(behaviorEvent => behaviorEvent.ClientEventId)
            .HasColumnName("client_event_id")
            .IsRequired();

        builder.HasOne(behaviorEvent => behaviorEvent.TravelerUser)
            .WithMany()
            .HasForeignKey(behaviorEvent => behaviorEvent.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(behaviorEvent => behaviorEvent.PointOfInterest)
            .WithMany()
            .HasForeignKey(behaviorEvent => behaviorEvent.PointOfInterestId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(behaviorEvent => behaviorEvent.Itinerary)
            .WithMany()
            .HasForeignKey(behaviorEvent => behaviorEvent.ItineraryId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(behaviorEvent => new
        {
            behaviorEvent.TravelerUserId,
            behaviorEvent.ClientEventId,
        })
            .IsUnique()
            .HasDatabaseName("UQ_RecommendationBehaviorEvents_Traveler_Client");
        builder.HasIndex(behaviorEvent => new
        {
            behaviorEvent.TravelerUserId,
            behaviorEvent.OccurredAtUtc,
        })
            .HasDatabaseName("IX_RecommendationBehaviorEvents_Traveler_OccurredAt");
        builder.HasIndex(behaviorEvent => behaviorEvent.PointOfInterestId)
            .HasDatabaseName("IX_RecommendationBehaviorEvents_POI");
        builder.HasIndex(behaviorEvent => behaviorEvent.ItineraryId)
            .HasDatabaseName("IX_RecommendationBehaviorEvents_Itinerary");
    }
}