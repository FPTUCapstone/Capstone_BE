using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutReconciliationItemConfiguration : IEntityTypeConfiguration<PayoutReconciliationItem>
{
    public void Configure(EntityTypeBuilder<PayoutReconciliationItem> builder)
    {
        builder.ToTable("PayoutReconciliationItem", "payment", table =>
            table.HasCheckConstraint("CK_PayoutReconciliationItem_outcome",
                "observed_outcome IN (N'Succeeded', N'Rejected')"));
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(item => item.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(item => item.DetectedAtUtc).HasColumnName("detected_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.ExpectedStatus).HasColumnName("expected_status").HasMaxLength(24).IsRequired();
        builder.Property(item => item.ObservedOutcome).HasColumnName("observed_outcome").HasMaxLength(24).IsRequired();
        builder.Property(item => item.ObservedReference).HasColumnName("observed_reference").HasMaxLength(100);
        builder.Property(item => item.Note).HasColumnName("note").HasMaxLength(500);

        builder.HasOne<PayoutBatch>().WithMany().HasForeignKey(item => item.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => item.BatchId).HasDatabaseName("IX_PayoutReconciliationItem_Batch");
    }
}