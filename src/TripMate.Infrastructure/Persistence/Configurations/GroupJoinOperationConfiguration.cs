using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class GroupJoinOperationConfiguration
    : IEntityTypeConfiguration<GroupJoinOperation>
{
    public void Configure(EntityTypeBuilder<GroupJoinOperation> builder)
    {
        builder.ToTable("GroupJoinOperations", "social");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id)
            .HasColumnName("join_operation_id")
            .ValueGeneratedOnAdd();
        builder.Property(operation => operation.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(operation => operation.GroupId)
            .HasColumnName("group_id")
            .IsRequired();
        builder.Property(operation => operation.InvitationId)
            .HasColumnName("invitation_id")
            .IsRequired();
        builder.Property(operation => operation.InvitationCode)
            .HasColumnName("invitation_code")
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(operation => operation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();
        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasIndex(operation => new { operation.TravelerUserId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UQ_GroupJoinOperations_TravelerKey");

        builder.HasOne(operation => operation.Invitation)
            .WithMany()
            .HasForeignKey(operation => operation.InvitationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(operation => operation.TravelGroup)
            .WithMany()
            .HasForeignKey(operation => operation.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(operation => operation.TravelerUser)
            .WithMany()
            .HasForeignKey(operation => operation.TravelerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
