using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ReroutingEventConfiguration : IEntityTypeConfiguration<ReroutingEvent>
{
    public void Configure(EntityTypeBuilder<ReroutingEvent> builder)
    {
        builder.ToTable("ReroutingEvents", "trip");
        builder.HasKey(reroutingEvent => reroutingEvent.Id);
        builder.Property(reroutingEvent => reroutingEvent.Id).HasColumnName("rerouting_id").ValueGeneratedOnAdd();
        builder.Property(reroutingEvent => reroutingEvent.IncidentId).HasColumnName("incident_id").IsRequired();
        builder.Property(reroutingEvent => reroutingEvent.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(reroutingEvent => reroutingEvent.ProposedItinerarySnapshot).HasColumnName("proposed_itinerary_snapshot").IsRequired();
        builder.Property(reroutingEvent => reroutingEvent.Status).HasColumnName("status").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(reroutingEvent => reroutingEvent.ProposedAtUtc).HasColumnName("proposed_at").AsUtcDateTime2().IsRequired();
        builder.Property(reroutingEvent => reroutingEvent.DecidedAtUtc).HasColumnName("decided_at").AsUtcDateTime2();
        builder.HasOne(reroutingEvent => reroutingEvent.Incident).WithMany().HasForeignKey(reroutingEvent => reroutingEvent.IncidentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(reroutingEvent => reroutingEvent.Session).WithMany().HasForeignKey(reroutingEvent => reroutingEvent.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}