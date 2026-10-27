using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class GroupMemberRemovalOperationConfiguration : IEntityTypeConfiguration<GroupMemberRemovalOperation>
{
    public void Configure(EntityTypeBuilder<GroupMemberRemovalOperation> builder)
    {
        builder.ToTable("GroupMemberRemovalOperations", "social");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasColumnName("remove_operation_id").ValueGeneratedOnAdd();
        builder.Property(operation => operation.HostUserId).HasColumnName("host_user_id").IsRequired();
        builder.Property(operation => operation.GroupId).HasColumnName("group_id").IsRequired();
        builder.Property(operation => operation.MemberUserId).HasColumnName("member_user_id").IsRequired();
        builder.Property(operation => operation.MemberName).HasColumnName("member_name")
            .HasMaxLength(GroupMemberRemovalOperation.MaxMemberNameLength).IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(operation => operation.RemovedAtUtc).HasColumnName("removed_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(operation => new { operation.HostUserId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UQ_GroupMemberRemovalOperations_HostKey");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.HostUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.MemberUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TravelGroup>()
            .WithMany()
            .HasForeignKey(operation => operation.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}