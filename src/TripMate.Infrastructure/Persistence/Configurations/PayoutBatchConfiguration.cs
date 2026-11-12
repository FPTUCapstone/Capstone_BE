using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutBatchConfiguration : IEntityTypeConfiguration<PayoutBatch>
{
    public void Configure(EntityTypeBuilder<PayoutBatch> builder)
    {
        builder.ToTable("PayoutBatch", "payment");
        builder.HasKey(batch => batch.Id);
        builder.Property(batch => batch.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(batch => batch.RunDate).HasColumnName("run_date").IsRequired();
        builder.Property(batch => batch.TourOperatorUserId).HasColumnName("tour_operator_user_id").IsRequired();
        builder.Property(batch => batch.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(batch => batch.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
        builder.Property(batch => batch.TransferReference).HasColumnName("transfer_reference").HasMaxLength(100);
        builder.Property(batch => batch.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(batch => batch.LastAttemptAtUtc).HasColumnName("last_attempt_at_utc").AsUtcDateTime2();
        builder.Property(batch => batch.CreatedAtUtc).HasColumnName("created_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(batch => batch.UpdatedAtUtc).HasColumnName("updated_at_utc").AsUtcDateTime2().IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(batch => batch.TourOperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(batch => new { batch.RunDate, batch.TourOperatorUserId })
            .IsUnique().HasDatabaseName("UX_PayoutBatch_RunDate_Operator");
        builder.HasIndex(batch => batch.Status).HasDatabaseName("IX_PayoutBatch_Status");

        builder.HasMany(batch => batch.Settlements)
            .WithOne()
            .HasForeignKey(settlement => settlement.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(batch => batch.Settlements).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}