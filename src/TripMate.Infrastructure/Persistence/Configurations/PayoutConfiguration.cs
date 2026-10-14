using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> builder)
    {
        builder.ToTable("Payouts", "payment", table =>
        {
            table.HasCheckConstraint(
                "CK_Payouts_PeriodOrder",
                "period_end >= period_start");
            table.HasCheckConstraint(
                "CK_Payouts_NetAmount",
                "net_amount = gross_revenue - commission_amount");
            table.HasCheckConstraint(
                "CK_Payouts_Status",
                "status IN ('PendingReview','ApprovedForSettlement','SettlementProcessing','Settled','SettlementFailed','Rejected')");
            table.HasCheckConstraint(
                "CK_Payouts_LifecycleState",
                "gateway_attempt_count >= 0");
            table.HasCheckConstraint(
                "CK_Payouts_BeneficiarySnapshot",
                "(beneficiary_bank_name IS NULL AND beneficiary_account_holder IS NULL AND beneficiary_account_last4 IS NULL) "
                + "OR (beneficiary_bank_name IS NOT NULL AND beneficiary_account_holder IS NOT NULL "
                + "AND beneficiary_account_last4 IS NOT NULL AND beneficiary_account_last4 NOT LIKE '%[^0-9]%')");
        });
        builder.HasKey(payout => payout.Id);
        builder.Property(payout => payout.Id).HasColumnName("payout_id").ValueGeneratedOnAdd();
        builder.Property(payout => payout.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(payout => payout.PeriodStart).HasColumnName("period_start").IsRequired();
        builder.Property(payout => payout.PeriodEnd).HasColumnName("period_end").IsRequired();
        builder.Property(payout => payout.GrossRevenue).HasColumnName("gross_revenue").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.CommissionAmount).HasColumnName("commission_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.NetAmount).HasColumnName("net_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.Status)
            .HasColumnName("status")
            .HasMaxLength(Payout.StatusMaxLength)
            .IsUnicode(false)
            .IsRequired()
            // I02/UC-73/UC-74: optimistic concurrency on the lifecycle status. Approve and Reject
            // both load a PendingReview row and update it; the second writer's SaveChangesAsync
            // throws DbUpdateConcurrencyException instead of silently overwriting the first
            // writer's transition (same pattern as UserConfiguration.Status and
            // OperatorProfileConfiguration.ApprovalStatus).
            .IsConcurrencyToken();
        builder.Property(payout => payout.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2();
        builder.Property(payout => payout.ReviewedBy).HasColumnName("reviewed_by");
        builder.Property(payout => payout.ReviewedAtUtc).HasColumnName("reviewed_at").AsUtcDateTime2();
        builder.Property(payout => payout.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasMaxLength(Payout.RejectionReasonMaxLength);
        builder.Property(payout => payout.SettlementAuthorizedBy).HasColumnName("settlement_authorized_by");
        builder.Property(payout => payout.SettlementAuthorizedAtUtc)
            .HasColumnName("settlement_authorized_at")
            .AsUtcDateTime2();
        builder.Property(payout => payout.GatewayReference)
            .HasColumnName("gateway_reference")
            .HasMaxLength(Payout.GatewayReferenceMaxLength);
        builder.Property(payout => payout.GatewayFailureReason)
            .HasColumnName("gateway_failure_reason")
            .HasMaxLength(Payout.GatewayFailureReasonMaxLength);
        builder.Property(payout => payout.GatewayAttemptCount)
            .HasColumnName("gateway_attempt_count")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(payout => payout.LastGatewayAttemptAtUtc)
            .HasColumnName("last_gateway_attempt_at")
            .AsUtcDateTime2();
        builder.Property(payout => payout.BeneficiaryBankName)
            .HasColumnName("beneficiary_bank_name")
            .HasMaxLength(Payout.BeneficiaryBankNameMaxLength);
        builder.Property(payout => payout.BeneficiaryAccountHolder)
            .HasColumnName("beneficiary_account_holder")
            .HasMaxLength(Payout.BeneficiaryAccountHolderMaxLength);
        builder.Property(payout => payout.BeneficiaryAccountLast4)
            .HasColumnName("beneficiary_account_last4")
            .HasColumnType("char(4)")
            .IsFixedLength()
            .HasMaxLength(Payout.BeneficiaryAccountLast4Length);
        builder.HasOne(payout => payout.Operator).WithMany()
            .HasForeignKey(payout => payout.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(payout => new { payout.OperatorUserId, payout.Status })
            .HasDatabaseName("IX_Payouts_Operator");
    }
}