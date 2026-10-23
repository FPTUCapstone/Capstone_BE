using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripSessionItemConfiguration : IEntityTypeConfiguration<TripSessionItem>
{
    public void Configure(EntityTypeBuilder<TripSessionItem> builder)
    {
        builder.ToTable("TripSessionItems", "trip");
        builder.HasKey(item => new { item.SessionId, item.ItineraryItemId });
        builder.Property(item => item.SessionId).HasColumnName("session_id");
        builder.Property(item => item.ItineraryItemId).HasColumnName("itinerary_item_id");
        builder.Property(item => item.SequenceNo).HasColumnName("sequence_no").IsRequired();
        builder.Property(item => item.PoiId).HasColumnName("poi_id").IsRequired();
        builder.Property(item => item.PoiName)
            .HasColumnName("poi_name")
            .HasMaxLength(PointOfInterest.NameMaxLength)
            .IsRequired();
        builder.Property(item => item.Latitude).HasColumnName("latitude").HasPrecision(9, 6).IsRequired();
        builder.Property(item => item.Longitude).HasColumnName("longitude").HasPrecision(9, 6).IsRequired();
        builder.Property(item => item.PlannedArrivalUtc).HasColumnName("planned_arrival").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.PlannedDepartureUtc).HasColumnName("planned_departure").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.IsMandatory).HasColumnName("is_mandatory").IsRequired();
        builder.Property(item => item.ReachedAtUtc).HasColumnName("reached_at").AsUtcDateTime2();
        builder.Property(item => item.SkippedAtUtc).HasColumnName("skipped_at").AsUtcDateTime2();
        builder.Ignore(item => item.Status);

        builder.HasOne(item => item.Session)
            .WithMany(session => session.Items)
            .HasForeignKey(item => item.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.ItineraryItem)
            .WithMany()
            .HasForeignKey(item => item.ItineraryItemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.SessionId, item.SequenceNo })
            .HasDatabaseName("UQ_TripSessionItems_Sequence")
            .IsUnique();
        builder.HasIndex(item => new { item.SessionId, item.ReachedAtUtc, item.SequenceNo })
            .HasDatabaseName("IX_TripSessionItems_Next");
    }
}