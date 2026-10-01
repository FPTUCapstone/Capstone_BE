using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class GroupLocationConfiguration : IEntityTypeConfiguration<GroupLocation>
{
    public void Configure(EntityTypeBuilder<GroupLocation> builder)
    {
        builder.ToTable("GroupLocationSharing", "social");
        builder.HasKey(location => location.Id);
        builder.Property(location => location.Id).HasColumnName("location_id").ValueGeneratedOnAdd();
        builder.Property(location => location.GroupId).HasColumnName("group_id");
        builder.Property(location => location.UserId).HasColumnName("user_id");
        builder.Property(location => location.Latitude).HasColumnName("latitude").HasPrecision(9, 6);
        builder.Property(location => location.Longitude).HasColumnName("longitude").HasPrecision(9, 6);
        builder.Property(location => location.RecordedAtUtc).HasColumnName("recorded_at").AsUtcDateTime2();
        builder.HasIndex(location => new { location.GroupId, location.RecordedAtUtc })
            .HasDatabaseName("IX_GroupLocationSharing_Group");
    }
}