using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/**
 * [UC-17] TravelGroup EF Core Configuration
 * Maps social.TravelGroups in database/tripmate_schema_v7.sql.
 */
public class TravelGroupConfiguration : IEntityTypeConfiguration<TravelGroup>
{
    public void Configure(EntityTypeBuilder<TravelGroup> builder)
    {
        builder.ToTable("TravelGroups", "social");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasColumnName("group_id").ValueGeneratedOnAdd();

        builder.Property(g => g.ItineraryId).HasColumnName("itinerary_id").IsRequired();
        builder.Property(g => g.HostUserId).HasColumnName("host_user_id").IsRequired();
        builder.Property(g => g.Name).HasColumnName("name").HasMaxLength(150);
        builder.Property(g => g.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();

        builder.HasOne(g => g.Itinerary)
            .WithMany(i => i.TravelGroups)
            .HasForeignKey(g => g.ItineraryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(g => g.HostUser)
            .WithMany()
            .HasForeignKey(g => g.HostUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(g => g.GroupMembers)
            .WithOne(m => m.TravelGroup)
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(g => g.GroupInvitations)
            .WithOne(i => i.TravelGroup)
            .HasForeignKey(i => i.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}