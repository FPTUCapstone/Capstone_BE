using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutSettlementAdjustmentConfiguration : IEntityTypeConfiguration<PayoutSettlementAdjustment>
{
    public void Configure(EntityTypeBuilder<PayoutSettlementAdjustment> builder)
    {
        builder.ToTable("PayoutSettlementAdjustment", "payment", table =>
            table.HasCheckConstraint("CK_PayoutSettlementAdjustment_amount", "amount > 0"));
        builder.HasKey(adjustment => adjustment.Id);
        builder.Property(adjustment => adjustment.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(adjustment => adjustment.SourceSettlementId).HasColumnName("source_settlement_id").IsRequired();
        builder.Property(adjustment => adjustment.TourOperatorUserId).HasColumnName("tour_operator_user_id").IsRequired();
        builder.Property(adjustment => adjustment.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        builder.Property(adjustment => adjustment.Reason).HasColumnName("reason").HasMaxLength(200).IsRequired();
        builder.Property(adjustment => adjustment.CreatedAtUtc).HasColumnName("created_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(adjustment => adjustment.AppliedBatchId).HasColumnName("applied_batch_id");
        builder.Property(adjustment => adjustment.AppliedAtUtc).HasColumnName("applied_at_utc").AsUtcDateTime2();

        builder.HasOne<PayoutSettlement>().WithMany().HasForeignKey(adjustment => adjustment.SourceSettlementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(adjustment => adjustment.TourOperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PayoutBatch>().WithMany().HasForeignKey(adjustment => adjustment.AppliedBatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(adjustment => adjustment.SourceSettlementId).IsUnique().HasDatabaseName("UX_PayoutSettlementAdjustment_Source");
        builder.HasIndex(adjustment => adjustment.TourOperatorUserId)
            .HasDatabaseName("IX_PayoutSettlementAdjustment_Operator_Outstanding")
            .HasFilter("[applied_batch_id] IS NULL");
    }
}