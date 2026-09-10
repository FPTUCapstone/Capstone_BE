using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/**
 * [UC-17] GroupInvitation EF Core Configuration
 * Maps social.GroupInvitations in database/tripmate_schema_v7.sql.
 */
public class GroupInvitationConfiguration : IEntityTypeConfiguration<GroupInvitation>
{
    public void Configure(EntityTypeBuilder<GroupInvitation> builder)
    {
        builder.ToTable("GroupInvitations", "social");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("invitation_id").ValueGeneratedOnAdd();

        builder.Property(i => i.GroupId).HasColumnName("group_id").IsRequired();
        builder.Property(i => i.InviteCode).HasColumnName("invite_code").HasMaxLength(20).IsRequired();
        builder.Property(i => i.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(i => i.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2().IsRequired();
        builder.Property(i => i.MaxUses).HasColumnName("max_uses").HasDefaultValue(50).IsRequired();
        builder.Property(i => i.UsedCount).HasColumnName("used_count").HasDefaultValue(0).IsRequired();
        builder.Property(i => i.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(i => i.InviteCode).IsUnique().HasDatabaseName("UX_GroupInvitations_InviteCode");

        builder.HasOne(i => i.TravelGroup)
            .WithMany(g => g.GroupInvitations)
            .HasForeignKey(i => i.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.CreatorUser)
            .WithMany()
            .HasForeignKey(i => i.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}