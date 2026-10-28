using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("Incidents", "trip");
        builder.HasKey(incident => incident.Id);
        builder.Property(incident => incident.Id).HasColumnName("incident_id").ValueGeneratedOnAdd();
        builder.Property(incident => incident.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(incident => incident.IncidentType).HasColumnName("incident_type").HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(incident => incident.WeatherEventId).HasColumnName("weather_event_id");
        builder.Property(incident => incident.Description).HasColumnName("description").HasMaxLength(Incident.DescriptionMaxLength);
        builder.Property(incident => incident.AffectedItineraryItemId).HasColumnName("affected_itinerary_item_id");
        builder.Property(incident => incident.SourceKey).HasColumnName("source_key").HasMaxLength(Incident.SourceKeyMaxLength);
        builder.Property(incident => incident.ClientEventId).HasColumnName("client_event_id");
        builder.Property(incident => incident.DetectedAtUtc).HasColumnName("detected_at").AsUtcDateTime2().IsRequired();
        builder.Property(incident => incident.ResolvedAtUtc).HasColumnName("resolved_at").AsUtcDateTime2();
        builder.HasOne(incident => incident.Session).WithMany().HasForeignKey(incident => incident.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(incident => incident.AffectedItem)
            .WithMany()
            .HasForeignKey(incident => new { incident.SessionId, incident.AffectedItineraryItemId })
            .HasPrincipalKey(item => new { item.SessionId, item.ItineraryItemId })
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(incident => new { incident.SessionId, incident.IncidentType, incident.SourceKey })
            .IsUnique()
            .HasFilter("[resolved_at] IS NULL AND [source_key] IS NOT NULL")
            .HasDatabaseName("UQ_Incidents_OpenSource");
        builder.HasIndex(incident => new { incident.SessionId, incident.ClientEventId })
            .IsUnique()
            .HasFilter("[client_event_id] IS NOT NULL")
            .HasDatabaseName("UQ_Incidents_ClientEvent");
    }
}