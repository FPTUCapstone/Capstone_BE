using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectRefundCaseConfiguration : IEntityTypeConfiguration<DirectRefundCase>
{
    public void Configure(EntityTypeBuilder<DirectRefundCase> builder)
    {
        builder.ToTable("DirectRefundCases", "payment");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("case_id").ValueGeneratedOnAdd();
        builder.Property(c => c.OperationId).HasColumnName("operation_id").IsRequired();
        builder.Property(c => c.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(c => c.SourceType).HasColumnName("source_type").HasMaxLength(30).IsUnicode(false).IsRequired();
        builder.Property(c => c.SourceRefId).HasColumnName("source_ref_id");
        builder.Property(c => c.RefundAmountVnd).HasColumnName("refund_amount_vnd").IsRequired();
        builder.Property(c => c.PaidAmountVnd).HasColumnName("paid_amount_vnd").IsRequired();
        builder.Property(c => c.RecipientBankCode).HasColumnName("recipient_bank_code")
            .HasMaxLength(DirectRefundCase.RecipientBankCodeMaxLength).IsUnicode(false);
        builder.Property(c => c.RecipientAccountNumber).HasColumnName("recipient_account_number")
            .HasMaxLength(DirectRefundCase.RecipientAccountNumberMaxLength);
        builder.Property(c => c.RecipientAccountHolder).HasColumnName("recipient_account_holder")
            .HasMaxLength(DirectRefundCase.RecipientAccountHolderMaxLength);
        builder.Property(c => c.RecipientConfirmedAtUtc).HasColumnName("recipient_confirmed_at").AsUtcDateTime2();
        builder.Property(c => c.Status).HasColumnName("status").HasMaxLength(40).IsUnicode(false).IsRequired();
        builder.Property(c => c.TransferDeadlineAtUtc).HasColumnName("transfer_deadline_at").AsUtcDateTime2();
        builder.Property(c => c.IsOverdueFlag).HasColumnName("is_overdue_flag").IsRequired();
        builder.Property(c => c.CompletedAtUtc).HasColumnName("completed_at").AsUtcDateTime2();
        builder.Property(c => c.CompletionMethod).HasColumnName("completion_method")
            .HasMaxLength(30).IsUnicode(false);
        builder.Property(c => c.CompletionRuleVersion).HasColumnName("completion_rule_version")
            .HasMaxLength(DirectRefundCase.CompletionRuleVersionMaxLength).IsUnicode(false);
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.Property(c => c.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasOne<Booking>().WithMany()
            .HasForeignKey(c => c.BookingId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.OperationId).IsUnique().HasDatabaseName("UQ_DirectRefundCases_OperationId");
        builder.HasIndex(c => c.BookingId).HasDatabaseName("IX_DirectRefundCases_Booking");
        builder.HasIndex(c => new { c.Status, c.TransferDeadlineAtUtc })
            .HasDatabaseName("IX_DirectRefundCases_Status");
    }
}
