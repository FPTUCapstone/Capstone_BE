using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class WeatherEventConfiguration : IEntityTypeConfiguration<WeatherEvent>
{
    public void Configure(EntityTypeBuilder<WeatherEvent> builder)
    {
        builder.ToTable("WeatherEvents", "trip");
        builder.HasKey(weatherEvent => weatherEvent.Id);
        builder.Property(weatherEvent => weatherEvent.Id).HasColumnName("weather_event_id").ValueGeneratedOnAdd();
        builder.Property(weatherEvent => weatherEvent.RegionName).HasColumnName("region_name").HasMaxLength(150);
        builder.Property(weatherEvent => weatherEvent.Latitude).HasColumnName("latitude").HasPrecision(9, 6);
        builder.Property(weatherEvent => weatherEvent.Longitude).HasColumnName("longitude").HasPrecision(9, 6);
        builder.Property(weatherEvent => weatherEvent.EventType).HasColumnName("event_type").HasMaxLength(WeatherEvent.EventTypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(weatherEvent => weatherEvent.Severity).HasColumnName("severity").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(weatherEvent => weatherEvent.Description).HasColumnName("description")
            .HasMaxLength(WeatherEvent.DescriptionMaxLength);
        builder.Property(weatherEvent => weatherEvent.ValidFromUtc).HasColumnName("valid_from").AsUtcDateTime2().IsRequired();
        builder.Property(weatherEvent => weatherEvent.ValidToUtc).HasColumnName("valid_to").AsUtcDateTime2();
        builder.Property(weatherEvent => weatherEvent.Source).HasColumnName("source").HasMaxLength(50).IsUnicode(false).IsRequired();
        builder.Property(weatherEvent => weatherEvent.IngestedAtUtc).HasColumnName("ingested_at").AsUtcDateTime2().IsRequired();

        // UC-70 additions
        builder.Property(weatherEvent => weatherEvent.Provider).HasColumnName("provider")
            .HasMaxLength(WeatherEvent.ProviderMaxLength).IsUnicode(false);
        builder.Property(weatherEvent => weatherEvent.ProviderEventId).HasColumnName("provider_event_id")
            .HasMaxLength(WeatherEvent.ProviderEventIdMaxLength).IsUnicode(false);
        builder.Property(weatherEvent => weatherEvent.OriginalAuthority).HasColumnName("original_authority")
            .HasMaxLength(WeatherEvent.OriginalAuthorityMaxLength);
        builder.Property(weatherEvent => weatherEvent.OfficialVietnamRiskLevel).HasColumnName("official_vietnam_risk_level");
        builder.Property(weatherEvent => weatherEvent.VerifiedDirective).HasColumnName("verified_directive")
            .HasMaxLength(WeatherEvent.VerifiedDirectiveMaxLength).IsUnicode(false);
        builder.Property(weatherEvent => weatherEvent.IsWithdrawn).HasColumnName("is_withdrawn").IsRequired();
        builder.Property(weatherEvent => weatherEvent.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();

        builder.HasIndex(weatherEvent => new { weatherEvent.Provider, weatherEvent.ProviderEventId }).IsUnique()
            .HasFilter("[provider] IS NOT NULL AND [provider_event_id] IS NOT NULL")
            .HasDatabaseName("UX_WeatherEvents_ProviderEvent");
    }
}