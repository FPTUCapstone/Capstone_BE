using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripSessionConfiguration : IEntityTypeConfiguration<TripSession>
{
    public void Configure(EntityTypeBuilder<TripSession> builder)
    {
        builder.ToTable("TripSessions", "trip");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).HasColumnName("session_id").ValueGeneratedOnAdd();
        builder.Property(session => session.ItineraryId).HasColumnName("itinerary_id").IsRequired();
        builder.Property(session => session.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(session => session.FsmState).HasColumnName("fsm_state").HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(session => session.CurrentLatitude).HasColumnName("current_latitude").HasPrecision(9, 6);
        builder.Property(session => session.CurrentLongitude).HasColumnName("current_longitude").HasPrecision(9, 6);
        builder.Property(session => session.StartedAtUtc).HasColumnName("started_at").AsUtcDateTime2();
        builder.Property(session => session.EndedAtUtc).HasColumnName("ended_at").AsUtcDateTime2();
        builder.Property(session => session.LastSyncedAtUtc).HasColumnName("last_synced_at").AsUtcDateTime2();

        builder.HasOne(session => session.Itinerary).WithMany().HasForeignKey(session => session.ItineraryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(session => session.TravelerUser).WithMany().HasForeignKey(session => session.TravelerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(session => new { session.TravelerUserId, session.FsmState }).HasDatabaseName("IX_TripSessions_Traveler");
        builder.HasIndex(session => session.ItineraryId).HasDatabaseName("IX_TripSessions_Itinerary");
    }
}