using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutSettlementAttemptConfiguration : IEntityTypeConfiguration<PayoutSettlementAttempt>
{
    public void Configure(EntityTypeBuilder<PayoutSettlementAttempt> builder)
    {
        builder.ToTable("PayoutSettlementAttempt", "payment");
        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(attempt => attempt.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(attempt => attempt.AttemptNumber).HasColumnName("attempt_number").IsRequired();
        builder.Property(attempt => attempt.SentAtUtc).HasColumnName("sent_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(attempt => attempt.GatewayReference).HasColumnName("gateway_reference").HasMaxLength(100).IsRequired();
        builder.Property(attempt => attempt.Outcome).HasColumnName("outcome").HasMaxLength(24).IsRequired();
        builder.Property(attempt => attempt.FailureReason).HasColumnName("failure_reason").HasMaxLength(500);

        builder.HasOne<PayoutBatch>().WithMany().HasForeignKey(attempt => attempt.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(attempt => new { attempt.BatchId, attempt.AttemptNumber })
            .IsUnique().HasDatabaseName("UX_PayoutSettlementAttempt_Batch_Number");
    }
}
