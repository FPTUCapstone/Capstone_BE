using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ItineraryItemConfiguration : IEntityTypeConfiguration<ItineraryItem>
{
    public void Configure(EntityTypeBuilder<ItineraryItem> builder)
    {
        builder.ToTable("ItineraryItems", "planning");

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("item_id").ValueGeneratedOnAdd();
        builder.Property(item => item.ItineraryId).HasColumnName("itinerary_id").IsRequired();
        builder.Property(item => item.PointOfInterestId).HasColumnName("poi_id");
        builder.Property(item => item.SequenceNo).HasColumnName("sequence_no").IsRequired();
        builder.Property(item => item.Kind)
            .HasColumnName("item_kind")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(item => item.PlannedArrivalUtc)
            .HasColumnName("planned_arrival")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(item => item.PlannedDepartureUtc)
            .HasColumnName("planned_departure")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(item => item.StayDurationMinutes)
            .HasColumnName("stay_duration_minutes")
            .IsRequired();
        builder.Property(item => item.TravelDurationToNextMinutes)
            .HasColumnName("travel_duration_to_next_minutes");
        builder.Property(item => item.IsMandatory).HasColumnName("is_mandatory").IsRequired();
        builder.Property(item => item.EstimatedCost)
            .HasColumnName("estimated_cost")
            .HasPrecision(12, 2);
        builder.Property(item => item.RecommendationReason)
            .HasColumnName("recommendation_reason")
            .HasMaxLength(ItineraryItem.RecommendationReasonMaxLength);

        builder.HasOne(item => item.Itinerary)
            .WithMany(itinerary => itinerary.Items)
            .HasForeignKey(item => item.ItineraryId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.PointOfInterest)
            .WithMany()
            .HasForeignKey(item => item.PointOfInterestId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.ItineraryId, item.SequenceNo })
            .HasDatabaseName("UQ_ItineraryItems")
            .IsUnique();
    }
}