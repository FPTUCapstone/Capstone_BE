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
                "payable_net_before_adjustments IS NULL OR net_amount = payable_net_before_adjustments - adjustments_applied");
            table.HasCheckConstraint(
                "CK_Payouts_Status",
                "status IN ('PendingReview','ApprovedForSettlement','SettlementProcessing','Settled','SettlementFailed','Rejected','Cancelled')");
            table.HasCheckConstraint(
                "CK_Payouts_LifecycleState",
                "gateway_attempt_count >= 0");
            table.HasCheckConstraint(
                "CK_Payouts_PayableNet",
                "payable_net_before_adjustments IS NULL OR payable_net_before_adjustments - adjustments_applied >= 0");
        });
        builder.HasKey(payout => payout.Id);
        builder.Property(payout => payout.Id).HasColumnName("payout_id").ValueGeneratedOnAdd();
        builder.Property(payout => payout.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(payout => payout.PayoutCode).HasColumnName("payout_code").HasMaxLength(Payout.PayoutCodeMaxLength).IsUnicode(false);
        builder.Property(payout => payout.PeriodStart).HasColumnName("period_start").IsRequired();
        builder.Property(payout => payout.PeriodEnd).HasColumnName("period_end").IsRequired();
        // "available balance" model (2026-10-23): a payout can span several settlement months, so
        // the legacy single settlement_month column is no longer mapped — PeriodStart/PeriodEnd
        // hold the earliest/latest covered month, and PeriodBreakdowns holds each month's figures.
        builder.Property(payout => payout.PeriodCount).HasColumnName("period_count").HasDefaultValue(1).IsRequired();
        builder.Property(payout => payout.CutOffAtUtc).HasColumnName("cut_off_at_utc").AsUtcDateTime2();

        builder.Property(payout => payout.GrossRevenue).HasColumnName("gross_revenue").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.PaymentsReceived).HasColumnName("payments_received").HasPrecision(14, 2);
        builder.Property(payout => payout.DiscountsGiven).HasColumnName("discounts_given").HasPrecision(14, 2);
        builder.Property(payout => payout.RefundedAmount).HasColumnName("refunded_amount").HasPrecision(14, 2);
        builder.Property(payout => payout.NetSales).HasColumnName("net_sales").HasPrecision(14, 2);
        builder.Property(payout => payout.CommissionAmount).HasColumnName("commission_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.PayableNetBeforeAdjustments).HasColumnName("payable_net_before_adjustments").HasPrecision(14, 2);
        builder.Property(payout => payout.AdjustmentsApplied).HasColumnName("adjustments_applied").HasPrecision(14, 2).HasDefaultValue(0m).IsRequired();
        builder.Property(payout => payout.NetAmount).HasColumnName("net_amount").HasPrecision(14, 2).IsRequired();

        builder.Property(payout => payout.BookingCount).HasColumnName("booking_count").HasDefaultValue(0).IsRequired();
        builder.Property(payout => payout.ParticipantCount).HasColumnName("participant_count").HasDefaultValue(0).IsRequired();
        builder.Property(payout => payout.MinimumAmountChecked).HasColumnName("minimum_amount_checked").HasPrecision(14, 2);

        builder.Property(payout => payout.BankName).HasColumnName("bank_name").HasMaxLength(Payout.BankNameMaxLength);
        builder.Property(payout => payout.AccountHolderName).HasColumnName("account_holder_name").HasMaxLength(Payout.AccountHolderNameMaxLength);
        builder.Property(payout => payout.AccountNumberMasked).HasColumnName("account_number_masked").HasMaxLength(Payout.AccountNumberMaskedMaxLength);
        builder.Property(payout => payout.AccountNumberProtected).HasColumnName("account_number_protected").HasMaxLength(500);

        builder.Property(payout => payout.Status)
            .HasColumnName("status")
            .HasMaxLength(Payout.StatusMaxLength)
            .IsUnicode(false)
            .IsRequired()
            // UC-73/UC-74 (I02): two concurrent Staff decisions on the same PendingReview payout
            // race on this column. Making it a concurrency token means the losing SaveChangesAsync
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
        builder.Property(payout => payout.CancelledAtUtc).HasColumnName("cancelled_at").AsUtcDateTime2();

        builder.HasOne(payout => payout.Operator).WithMany()
            .HasForeignKey(payout => payout.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(payout => new { payout.OperatorUserId, payout.Status })
            .HasDatabaseName("IX_Payouts_Operator");
        builder.HasIndex(payout => payout.PayoutCode)
            .IsUnique()
            .HasDatabaseName("UX_Payouts_Code")
            .HasFilter("[payout_code] IS NOT NULL");
        // BR-113 (available balance model): at most one active payout per operator, not per month —
        // one open payout can cover several months at once.
        builder.HasIndex(payout => payout.OperatorUserId)
            .IsUnique()
            .HasDatabaseName("UX_Payouts_ActiveOperator")
            .HasFilter("[status] IN ('PendingReview','ApprovedForSettlement','SettlementProcessing','SettlementFailed')");

        builder.HasMany(payout => payout.Items).WithOne().HasForeignKey(item => item.PayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(payout => payout.RefundItems).WithOne().HasForeignKey(item => item.PayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(payout => payout.PeriodBreakdowns).WithOne().HasForeignKey(b => b.PayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(payout => payout.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(payout => payout.RefundItems).HasField("_refundItems").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(payout => payout.PeriodBreakdowns).HasField("_periodBreakdowns").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}