using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectRefundTransferConfiguration : IEntityTypeConfiguration<DirectRefundTransfer>
{
    public void Configure(EntityTypeBuilder<DirectRefundTransfer> builder)
    {
        builder.ToTable("DirectRefundTransfers", "payment");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("transfer_id").ValueGeneratedOnAdd();
        builder.Property(t => t.OperationId).HasColumnName("operation_id").IsRequired();
        builder.Property(t => t.CaseId).HasColumnName("case_id").IsRequired();
        builder.Property(t => t.ReportedAmountVnd).HasColumnName("reported_amount_vnd").IsRequired();
        builder.Property(t => t.BankReference).HasColumnName("bank_reference")
            .HasMaxLength(DirectRefundTransfer.BankReferenceMaxLength).IsRequired();
        builder.Property(t => t.ActualTransferredAtUtc).HasColumnName("actual_transferred_at")
            .AsUtcDateTime2().IsRequired();
        builder.Property(t => t.ReportedAtUtc).HasColumnName("reported_at").AsUtcDateTime2().IsRequired();
        builder.Property(t => t.ConfirmationDeadlineAtUtc).HasColumnName("confirmation_deadline_at")
            .AsUtcDateTime2().IsRequired();
        builder.Property(t => t.SnapshotBankCode).HasColumnName("snapshot_bank_code")
            .HasMaxLength(DirectRefundTransfer.SnapshotBankCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(t => t.SnapshotAccountNumber).HasColumnName("snapshot_account_number")
            .HasMaxLength(DirectRefundTransfer.SnapshotAccountNumberMaxLength).IsRequired();
        builder.Property(t => t.SnapshotAccountHolder).HasColumnName("snapshot_account_holder")
            .HasMaxLength(DirectRefundTransfer.SnapshotFieldMaxLength).IsRequired();
        builder.Property(t => t.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();

        builder.HasOne<DirectRefundCase>().WithMany()
            .HasForeignKey(t => t.CaseId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.OperationId).IsUnique().HasDatabaseName("UQ_DirectRefundTransfers_OperationId");
        builder.HasIndex(t => t.CaseId).HasDatabaseName("IX_DirectRefundTransfers_Case");
        builder.HasIndex(t => t.ConfirmationDeadlineAtUtc)
            .HasDatabaseName("IX_DirectRefundTransfers_ConfirmationDeadline");
    }
}