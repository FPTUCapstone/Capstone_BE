using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class GroupRollCallCheckinConfiguration : IEntityTypeConfiguration<GroupRollCallCheckin>
{
    public void Configure(EntityTypeBuilder<GroupRollCallCheckin> builder)
    {
        builder.ToTable("GroupRollCallCheckins", "social");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("checkin_id").ValueGeneratedOnAdd();

        builder.Property(c => c.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(c => c.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(c => c.CheckedInAtUtc).HasColumnName("checked_in_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(c => new { c.MessageId, c.UserId }).IsUnique();

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
