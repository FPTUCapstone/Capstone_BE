using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class PointOfInterestConfiguration : IEntityTypeConfiguration<PointOfInterest>
{
    public void Configure(EntityTypeBuilder<PointOfInterest> builder)
    {
        builder.ToTable("POIs", "catalog");

        builder.HasKey(poi => poi.Id);
        builder.Property(poi => poi.Id).HasColumnName("poi_id").ValueGeneratedOnAdd();
        builder.Property(poi => poi.CategoryId).HasColumnName("category_id").IsRequired();
        builder.Property(poi => poi.Name)
            .HasColumnName("name")
            .HasMaxLength(PointOfInterest.NameMaxLength)
            .IsRequired();
        builder.Property(poi => poi.Description)
            .HasColumnName("description")
            .HasMaxLength(PointOfInterest.DescriptionMaxLength);
        builder.Property(poi => poi.Latitude).HasColumnName("latitude").HasPrecision(9, 6);
        builder.Property(poi => poi.Longitude).HasColumnName("longitude").HasPrecision(9, 6);
        builder.Property(poi => poi.Address)
            .HasColumnName("address")
            .HasMaxLength(PointOfInterest.AddressMaxLength);
        builder.Property(poi => poi.IndoorOutdoor)
            .HasColumnName("indoor_outdoor")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false)
            .HasDefaultValueSql("'Outdoor'")
            .IsRequired();
        builder.Property(poi => poi.ScenicScore).HasColumnName("scenic_score").HasPrecision(3, 1);
        builder.Property(poi => poi.PhotoRating).HasColumnName("photo_rating").HasPrecision(3, 1);
        builder.Property(poi => poi.AverageVisitDurationMinutes)
            .HasColumnName("avg_visit_duration_minutes")
            .HasDefaultValue(PointOfInterest.DefaultAverageVisitDurationMinutes);
        builder.Property(poi => poi.HasShelter)
            .HasColumnName("has_shelter")
            .HasDefaultValue(false);
        builder.Property(poi => poi.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false)
            .HasDefaultValueSql("'Active'")
            .IsRequired();
        builder.Property(poi => poi.CreatedById).HasColumnName("created_by");
        builder.Property(poi => poi.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2();
        builder.Property(poi => poi.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2();

        builder.HasOne(poi => poi.Category)
            .WithMany()
            .HasForeignKey(poi => poi.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(poi => poi.CreatedById)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Navigation(poi => poi.OpeningHours).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(poi => poi.PoiTags).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(poi => poi.CategoryId).HasDatabaseName("IX_POIs_Category");
        builder.HasIndex(poi => new { poi.Latitude, poi.Longitude })
            .HasDatabaseName("IX_POIs_LatLng");
    }
}