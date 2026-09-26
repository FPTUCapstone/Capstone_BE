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
        builder.Property(incident => incident.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(incident => incident.DetectedAtUtc).HasColumnName("detected_at").AsUtcDateTime2().IsRequired();
        builder.Property(incident => incident.ResolvedAtUtc).HasColumnName("resolved_at").AsUtcDateTime2();
        builder.HasOne(incident => incident.Session).WithMany().HasForeignKey(incident => incident.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}