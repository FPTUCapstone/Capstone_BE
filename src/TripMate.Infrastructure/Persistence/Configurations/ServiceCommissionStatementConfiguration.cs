using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ServiceCommissionStatementConfiguration : IEntityTypeConfiguration<ServiceCommissionStatement>
{
    public void Configure(EntityTypeBuilder<ServiceCommissionStatement> builder)
    {
        builder.ToTable("ServiceCommissionStatements", "commercial");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("statement_id").ValueGeneratedOnAdd();
        builder.Property(s => s.StatementCode).HasColumnName("statement_code").HasMaxLength(ServiceCommissionStatement.StatementCodeMaxLength).IsRequired();
        builder.Property(s => s.ProviderId).HasColumnName("provider_id").IsRequired();
        builder.Property(s => s.Period).HasColumnName("period").HasMaxLength(ServiceCommissionStatement.PeriodMaxLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.TotalRequests).HasColumnName("total_requests").IsRequired();
        builder.Property(s => s.TotalActualAmount).HasColumnName("total_actual_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(s => s.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 2).HasDefaultValue(ServiceCommissionStatement.DefaultCommissionRate).IsRequired();
        builder.Property(s => s.CommissionAmount).HasColumnName("commission_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(s => s.IssueDate).HasColumnName("issue_date").IsRequired();
        builder.Property(s => s.DueDate).HasColumnName("due_date").IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasMaxLength(20).IsUnicode(false).HasDefaultValue(ServiceCommissionStatement.StatusIssued).IsRequired();
        builder.Property(s => s.BankTransferReference).HasColumnName("bank_transfer_reference").HasMaxLength(ServiceCommissionStatement.BankTransferReferenceMaxLength);
        builder.Property(s => s.ReportedAtUtc).HasColumnName("reported_at").AsUtcDateTime2();
        builder.Property(s => s.ConfirmedByUserId).HasColumnName("confirmed_by_user_id");
        builder.Property(s => s.ConfirmedAtUtc).HasColumnName("confirmed_at").AsUtcDateTime2();
        builder.Property(s => s.ReceiptNumber).HasColumnName("receipt_number").HasMaxLength(ServiceCommissionStatement.ReceiptNumberMaxLength);
        builder.Property(s => s.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(s => s.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasOne(s => s.Provider).WithMany().HasForeignKey(s => s.ProviderId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_ServiceCommissionStatements_Provider");
        builder.HasOne(s => s.ConfirmedByUser).WithMany().HasForeignKey(s => s.ConfirmedByUserId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_ServiceCommissionStatements_Staff");
        builder.HasMany(s => s.Bookings).WithOne(b => b.CommissionStatement).HasForeignKey(b => b.CommissionStatementId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(s => s.StatementCode).IsUnique().HasDatabaseName("UQ_ServiceCommissionStatements_StatementCode");
        builder.HasIndex(s => new { s.ProviderId, s.Period }).IsUnique().HasDatabaseName("UQ_ServiceCommissionStatements_Provider_Period");
        builder.HasIndex(s => s.ProviderId).HasDatabaseName("IX_ServiceCommissionStatements_Provider");
        builder.HasIndex(s => s.Period).HasDatabaseName("IX_ServiceCommissionStatements_Period");
        builder.HasIndex(s => s.Status).HasDatabaseName("IX_ServiceCommissionStatements_Status");
    }
}
