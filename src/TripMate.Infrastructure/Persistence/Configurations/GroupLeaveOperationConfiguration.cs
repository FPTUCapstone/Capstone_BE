using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class GroupLeaveOperationConfiguration : IEntityTypeConfiguration<GroupLeaveOperation>
{
    public void Configure(EntityTypeBuilder<GroupLeaveOperation> builder)
    {
        builder.ToTable("GroupLeaveOperations", "social");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasColumnName("leave_operation_id").ValueGeneratedOnAdd();
        builder.Property(operation => operation.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(operation => operation.GroupId).HasColumnName("group_id").IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(operation => operation.GroupClosed).HasColumnName("group_closed").IsRequired();
        builder.Property(operation => operation.NewHostUserId).HasColumnName("new_host_user_id");
        builder.Property(operation => operation.LeftAtUtc).HasColumnName("left_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(operation => new { operation.TravelerUserId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UQ_GroupLeaveOperations_TravelerKey");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.TravelerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TravelGroup>()
            .WithMany()
            .HasForeignKey(operation => operation.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}