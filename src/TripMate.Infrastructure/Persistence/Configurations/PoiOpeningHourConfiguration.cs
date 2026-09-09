using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class PoiOpeningHourConfiguration : IEntityTypeConfiguration<PoiOpeningHour>
{
    public void Configure(EntityTypeBuilder<PoiOpeningHour> builder)
    {
        builder.ToTable("POIOpeningHours", "catalog");

        builder.HasKey(hours => new { hours.PointOfInterestId, hours.DayOfWeek });
        builder.Property(hours => hours.PointOfInterestId).HasColumnName("poi_id");
        builder.Property(hours => hours.DayOfWeek).HasColumnName("day_of_week");
        builder.Property(hours => hours.OpenTime).HasColumnName("open_time").HasColumnType("time");
        builder.Property(hours => hours.CloseTime).HasColumnName("close_time").HasColumnType("time");
        builder.Property(hours => hours.IsClosed)
            .HasColumnName("is_closed")
            .HasDefaultValue(false);

        builder.HasOne(hours => hours.PointOfInterest)
            .WithMany(poi => poi.OpeningHours)
            .HasForeignKey(hours => hours.PointOfInterestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}