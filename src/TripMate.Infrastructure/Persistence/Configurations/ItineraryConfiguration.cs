using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/**
 * [UC-17] Itinerary EF Core Configuration
 * Maps planning.Itineraries in database/tripmate_schema_v7.sql.
 */
public class ItineraryConfiguration : IEntityTypeConfiguration<Itinerary>
{
    public void Configure(EntityTypeBuilder<Itinerary> builder)
    {
        builder.ToTable("Itineraries", "planning");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("itinerary_id").ValueGeneratedOnAdd();

        builder.Property(i => i.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(i => i.SourceType).HasColumnName("source_type").HasMaxLength(14).IsRequired();
        builder.Property(i => i.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(i => i.Status).HasColumnName("status").HasMaxLength(10).IsRequired();
        builder.Property(i => i.ValidFromUtc).HasColumnName("valid_from").AsUtcDateTime2();
        builder.Property(i => i.ValidToUtc).HasColumnName("valid_to").AsUtcDateTime2();
        builder.Property(i => i.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(i => i.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();

        builder.HasOne(i => i.TravelerUser)
            .WithMany()
            .HasForeignKey(i => i.TravelerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}