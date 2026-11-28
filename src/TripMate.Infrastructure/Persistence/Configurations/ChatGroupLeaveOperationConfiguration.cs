using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;
namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ChatGroupLeaveOperationConfiguration : IEntityTypeConfiguration<ChatGroupLeaveOperation>
{
    public void Configure(EntityTypeBuilder<ChatGroupLeaveOperation> b)
    {
        b.ToTable("ChatGroupLeaveOperations", "social");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).HasColumnName("operation_id").ValueGeneratedOnAdd();
        b.Property(o => o.GroupId).HasColumnName("group_id");
        b.Property(o => o.ActorId).HasColumnName("actor_id");
        b.Property(o => o.ClientRequestId).HasColumnName("client_request_id");
        b.Property(o => o.ReplacementHostUserId).HasColumnName("replacement_host_user_id");
        b.Property(o => o.NewHostUserId).HasColumnName("new_host_user_id");
        b.Property(o => o.LeftAtUtc).HasColumnName("left_at_utc").AsUtcDateTime2();
        b.HasIndex(o => new { o.GroupId, o.ActorId, o.ClientRequestId }).IsUnique().HasDatabaseName(ChatGroupLeaveOperation.RetryIndex);
        b.HasOne(o => o.Group).WithMany().HasForeignKey(o => o.GroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(o => o.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(o => o.NewHostUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(o => o.ReplacementHostUserId).OnDelete(DeleteBehavior.Restrict);
    }
}