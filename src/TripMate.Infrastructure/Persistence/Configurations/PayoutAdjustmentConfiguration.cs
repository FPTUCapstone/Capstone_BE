using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutAdjustmentConfiguration : IEntityTypeConfiguration<PayoutAdjustment>
{
    public void Configure(EntityTypeBuilder<PayoutAdjustment> builder)
    {
        builder.ToTable("PayoutAdjustments", "payment", table =>
            table.HasCheckConstraint("CK_PayoutAdjustments_Amount", "amount < 0"));
        builder.HasKey(adjustment => adjustment.Id);
        builder.Property(adjustment => adjustment.Id).HasColumnName("adjustment_id").ValueGeneratedOnAdd();
        builder.Property(adjustment => adjustment.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(adjustment => adjustment.SourceRefundId).HasColumnName("source_refund_id").IsRequired();
        builder.Property(adjustment => adjustment.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.Property(adjustment => adjustment.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(adjustment => adjustment.AppliedPayoutId).HasColumnName("applied_payout_id");
        builder.Property(adjustment => adjustment.AppliedAmount).HasColumnName("applied_amount").HasPrecision(12, 2);
        builder.Property(adjustment => adjustment.AppliedAtUtc).HasColumnName("applied_at").AsUtcDateTime2();

        builder.HasOne<OperatorProfile>().WithMany().HasForeignKey(adjustment => adjustment.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Refund>().WithMany().HasForeignKey(adjustment => adjustment.SourceRefundId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payout>().WithMany().HasForeignKey(adjustment => adjustment.AppliedPayoutId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(adjustment => adjustment.SourceRefundId).IsUnique().HasDatabaseName("UQ_PayoutAdjustments_SourceRefund");
        builder.HasIndex(adjustment => adjustment.OperatorUserId)
            .HasDatabaseName("IX_PayoutAdjustments_Operator_Outstanding")
            .HasFilter("[applied_payout_id] IS NULL");
    }
}