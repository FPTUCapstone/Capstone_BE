using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class PoiTagConfiguration : IEntityTypeConfiguration<PoiTag>
{
    public void Configure(EntityTypeBuilder<PoiTag> builder)
    {
        builder.ToTable("POITagMap", "catalog");

        builder.HasKey(mapping => new { mapping.PointOfInterestId, mapping.TagId });
        builder.Property(mapping => mapping.PointOfInterestId).HasColumnName("poi_id");
        builder.Property(mapping => mapping.TagId).HasColumnName("tag_id");

        builder.HasOne(mapping => mapping.PointOfInterest)
            .WithMany(poi => poi.PoiTags)
            .HasForeignKey(mapping => mapping.PointOfInterestId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(mapping => mapping.Tag)
            .WithMany()
            .HasForeignKey(mapping => mapping.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}