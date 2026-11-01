using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutSettlementWorkItemConfiguration : IEntityTypeConfiguration<PayoutSettlementWorkItem>
{
    public void Configure(EntityTypeBuilder<PayoutSettlementWorkItem> builder)
    {
        builder.ToTable("PayoutSettlementWorkItems", "payment", table =>
        {
            table.HasCheckConstraint(
                "CK_PayoutSettlementWorkItems_State",
                "state IN ('Pending','Submitting','Submitted','Completed','ManualReconciliation')");
            table.HasCheckConstraint(
                "CK_PayoutSettlementWorkItems_Counts",
                "attempt_count >= 0 AND poll_count >= 0");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("work_item_id").ValueGeneratedOnAdd();
        builder.Property(item => item.PayoutId).HasColumnName("payout_id").IsRequired();
        builder.Property(item => item.State)
            .HasColumnName("state")
            .HasMaxLength(PayoutSettlementWorkItem.StateMaxLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(item => item.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0).IsRequired();
        builder.Property(item => item.NextAttemptAtUtc).HasColumnName("next_attempt_at").AsUtcDateTime2();
        builder.Property(item => item.LeaseToken).HasColumnName("lease_token");
        builder.Property(item => item.LeaseExpiresAtUtc).HasColumnName("lease_expires_at").AsUtcDateTime2();
        builder.Property(item => item.GatewayReference)
            .HasColumnName("gateway_reference")
            .HasMaxLength(PayoutSettlementWorkItem.GatewayReferenceMaxLength);
        builder.Property(item => item.SubmittedAtUtc).HasColumnName("submitted_at").AsUtcDateTime2();
        builder.Property(item => item.PollDeadlineAtUtc).HasColumnName("poll_deadline_at").AsUtcDateTime2();
        builder.Property(item => item.PollCount).HasColumnName("poll_count").HasDefaultValue(0).IsRequired();
        builder.Property(item => item.LastPolledAtUtc).HasColumnName("last_polled_at").AsUtcDateTime2();
        builder.Property(item => item.LastErrorCode)
            .HasColumnName("last_error_code")
            .HasMaxLength(PayoutSettlementWorkItem.LastErrorCodeMaxLength)
            .IsUnicode(false);
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.CompletedAtUtc).HasColumnName("completed_at").AsUtcDateTime2();
        builder.Property(item => item.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasOne<Payout>().WithMany().HasForeignKey(item => item.PayoutId).OnDelete(DeleteBehavior.Restrict);
        // One durable work item per payout: a double authorization can never queue two transfers.
        builder.HasIndex(item => item.PayoutId).IsUnique().HasDatabaseName("UX_PayoutSettlementWorkItems_Payout");
        builder.HasIndex(item => new { item.State, item.NextAttemptAtUtc }).HasDatabaseName("IX_PayoutSettlementWorkItems_Due");
    }
}
