using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class WeatherLocationMappingConfiguration : IEntityTypeConfiguration<WeatherLocationMapping>
{
    public void Configure(EntityTypeBuilder<WeatherLocationMapping> builder)
    {
        builder.ToTable("WeatherLocationMappings", "trip");
        builder.HasKey(mapping => mapping.Id);
        builder.Property(mapping => mapping.Id).HasColumnName("location_mapping_id").ValueGeneratedOnAdd();
        builder.Property(mapping => mapping.TripMateLocationKey).HasColumnName("trip_mate_location_key")
            .HasMaxLength(WeatherLocationMapping.KeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(mapping => mapping.TomorrowLocationId).HasColumnName("tomorrow_location_id")
            .HasMaxLength(WeatherLocationMapping.TomorrowIdMaxLength).IsUnicode(false).IsRequired();
        builder.Property(mapping => mapping.Latitude).HasColumnName("latitude").HasPrecision(9, 6).IsRequired();
        builder.Property(mapping => mapping.Longitude).HasColumnName("longitude").HasPrecision(9, 6).IsRequired();
        builder.Property(mapping => mapping.Name).HasColumnName("name")
            .HasMaxLength(WeatherLocationMapping.NameMaxLength);
        builder.Property(mapping => mapping.LastDepartureEndAtUtc).HasColumnName("last_departure_end_at").AsUtcDateTime2();
        builder.Property(mapping => mapping.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(mapping => mapping.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(mapping => mapping.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(mapping => mapping.TripMateLocationKey).IsUnique()
            .HasDatabaseName("UX_WeatherLocationMappings_Key");

        builder.HasIndex(mapping => mapping.TomorrowLocationId).IsUnique()
            .HasDatabaseName("UX_WeatherLocationMappings_TomorrowId");
    }
}