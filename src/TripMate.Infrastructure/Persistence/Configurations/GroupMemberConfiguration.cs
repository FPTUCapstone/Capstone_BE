using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/**
 * [UC-17] GroupMember EF Core Configuration
 * Maps social.GroupMembers in database/tripmate_schema_v7.sql with composite PK (group_id, user_id).
 */
public class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> builder)
    {
        builder.ToTable("GroupMembers", "social");

        builder.HasKey(m => new { m.GroupId, m.UserId });

        builder.Property(m => m.GroupId).HasColumnName("group_id");
        builder.Property(m => m.UserId).HasColumnName("user_id");

        builder.Property(m => m.LocationSharingEnabled)
            .HasColumnName("location_sharing_enabled")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(m => m.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(m => m.JoinedAtUtc)
            .HasColumnName("joined_at")
            .AsUtcDateTime2()
            .IsRequired();

        builder.Property(m => m.LeftAtUtc)
            .HasColumnName("left_at")
            .AsUtcDateTime2();

        builder.HasOne(m => m.TravelGroup)
            .WithMany(g => g.GroupMembers)
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}