using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class RefundManualCompletionOperationConfiguration
    : IEntityTypeConfiguration<RefundManualCompletionOperation>
{
    public void Configure(EntityTypeBuilder<RefundManualCompletionOperation> builder)
    {
        builder.ToTable("RefundManualCompletionOperations", "payment");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasColumnName("operation_id").ValueGeneratedOnAdd();
        builder.Property(operation => operation.RefundId).HasColumnName("refund_id").IsRequired();
        builder.Property(operation => operation.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(operation => operation.RequestHash).HasColumnName("request_hash").HasMaxLength(64)
            .IsUnicode(false).IsRequired();
        builder.Property(operation => operation.GatewayReference).HasColumnName("gateway_reference")
            .HasMaxLength(Refund.GatewayReferenceMaxLength).IsRequired();
        builder.Property(operation => operation.CompletedAtUtc).HasColumnName("completed_at").AsUtcDateTime2()
            .IsRequired();
        builder.HasIndex(operation => new { operation.ActorUserId, operation.IdempotencyKey }).IsUnique()
            .HasDatabaseName("UX_RefundManualCompletionOperations_ActorKey");
        builder.HasIndex(operation => operation.RefundId).IsUnique()
            .HasDatabaseName("UX_RefundManualCompletionOperations_Refund");
        builder.HasOne<Refund>().WithMany().HasForeignKey(operation => operation.RefundId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(operation => operation.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
