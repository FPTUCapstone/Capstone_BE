using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PoiVisitedMarkConfiguration : IEntityTypeConfiguration<PoiVisitedMark>
{
    public void Configure(EntityTypeBuilder<PoiVisitedMark> builder)
    {
        builder.ToTable("PoiVisitedMarks", "social");

        builder.HasKey(mark => mark.Id);
        builder.Property(mark => mark.Id)
            .HasColumnName("visited_mark_id")
            .ValueGeneratedOnAdd();
        builder.Property(mark => mark.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(mark => mark.PointOfInterestId)
            .HasColumnName("poi_id")
            .IsRequired();
        builder.Property(mark => mark.MarkedAtUtc)
            .HasColumnName("marked_at_utc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(mark => mark.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PointOfInterest>()
            .WithMany()
            .HasForeignKey(mark => mark.PointOfInterestId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(mark => new { mark.TravelerUserId, mark.PointOfInterestId })
            .IsUnique()
            .HasDatabaseName("UQ_PoiVisitedMarks_Traveler_POI");
    }
}