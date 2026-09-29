using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class PoiPhotoConfiguration : IEntityTypeConfiguration<PoiPhoto>
{
    public void Configure(EntityTypeBuilder<PoiPhoto> builder)
    {
        builder.ToTable("POIPhotos", "catalog");

        builder.HasKey(photo => photo.Id);
        builder.Property(photo => photo.Id).HasColumnName("photo_id").ValueGeneratedOnAdd();
        builder.Property(photo => photo.PointOfInterestId).HasColumnName("poi_id").IsRequired();
        builder.Property(photo => photo.Url)
            .HasColumnName("url")
            .HasMaxLength(PoiPhoto.UrlMaxLength)
            .IsRequired();
        builder.Property(photo => photo.Caption)
            .HasColumnName("caption")
            .HasMaxLength(PoiPhoto.CaptionMaxLength);
        builder.Property(photo => photo.SortOrder)
            .HasColumnName("sort_order")
            .HasDefaultValue(0);

        builder.HasOne(photo => photo.PointOfInterest)
            .WithMany()
            .HasForeignKey(photo => photo.PointOfInterestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(photo => photo.PointOfInterestId).HasDatabaseName("IX_POIPhotos_POI");
    }
}