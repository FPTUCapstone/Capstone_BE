using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PoiFavoriteConfiguration : IEntityTypeConfiguration<PoiFavorite>
{
    public void Configure(EntityTypeBuilder<PoiFavorite> builder)
    {
        builder.ToTable("PoiFavorites", "social");

        builder.HasKey(favorite => favorite.Id);
        builder.Property(favorite => favorite.Id)
            .HasColumnName("favorite_id")
            .ValueGeneratedOnAdd();
        builder.Property(favorite => favorite.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(favorite => favorite.PointOfInterestId)
            .HasColumnName("poi_id")
            .IsRequired();
        builder.Property(favorite => favorite.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(favorite => favorite.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PointOfInterest>()
            .WithMany()
            .HasForeignKey(favorite => favorite.PointOfInterestId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(favorite => new { favorite.TravelerUserId, favorite.PointOfInterestId })
            .IsUnique()
            .HasDatabaseName("UQ_PoiFavorites_Traveler_POI");
    }
}