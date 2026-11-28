using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public const string PairIndexName = "UX_Friendships_Pair";

    public void Configure(EntityTypeBuilder<Friendship> b)
    {
        b.ToTable("Friendships", "social", t =>
        {
            t.HasCheckConstraint("CK_Friendships_OrderedPair", "lower_user_id < upper_user_id");
            t.HasCheckConstraint("CK_Friendships_Requester", "requested_by_user_id IN (lower_user_id, upper_user_id)");
            t.HasCheckConstraint("CK_Friendships_Status", $"status IN ('{Friendship.Pending}', '{Friendship.Accepted}')");
        });
        b.HasKey(f => f.Id);
        b.Property(f => f.Id).HasColumnName("friendship_id").ValueGeneratedOnAdd();
        b.Property(f => f.LowerUserId).HasColumnName("lower_user_id");
        b.Property(f => f.UpperUserId).HasColumnName("upper_user_id");
        b.Property(f => f.RequestedByUserId).HasColumnName("requested_by_user_id").IsConcurrencyToken();
        b.Property(f => f.Status).HasColumnName("status").HasMaxLength(16).IsRequired().IsConcurrencyToken();
        b.Property(f => f.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasIndex(f => new { f.LowerUserId, f.UpperUserId }).IsUnique().HasDatabaseName(PairIndexName);
        b.HasIndex(f => new { f.UpperUserId, f.LowerUserId }).HasDatabaseName("IX_Friendships_Upper");
        b.HasOne(f => f.LowerUser).WithMany().HasForeignKey(f => f.LowerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(f => f.UpperUser).WithMany().HasForeignKey(f => f.UpperUserId).OnDelete(DeleteBehavior.Restrict);
    }
}