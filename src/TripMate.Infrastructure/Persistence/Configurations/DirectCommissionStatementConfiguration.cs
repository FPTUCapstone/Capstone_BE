using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectCommissionStatementConfiguration : IEntityTypeConfiguration<DirectCommissionStatement>
{
    public void Configure(EntityTypeBuilder<DirectCommissionStatement> builder)
    {
        builder.ToTable("TourDirectCommissionStatements", "commercial");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("statement_id").ValueGeneratedOnAdd();
        builder.Property(s => s.StatementCode).HasColumnName("statement_code")
            .HasMaxLength(DirectCommissionStatement.StatementCodeMaxLength).IsRequired();
        builder.Property(s => s.TourOperatorUserId).HasColumnName("tour_operator_user_id").IsRequired();
        builder.Property(s => s.Period).HasColumnName("period")
            .HasMaxLength(DirectCommissionStatement.PeriodMaxLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.EligibleBaseVnd).HasColumnName("eligible_base_vnd").IsRequired();
        builder.Property(s => s.CommissionRate).HasColumnName("commission_rate")
            .HasPrecision(5, 4).IsRequired();
        builder.Property(s => s.CommissionAmountVnd).HasColumnName("commission_amount_vnd").IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(s => s.PayosPaymentLinkId).HasColumnName("payos_payment_link_id")
            .HasMaxLength(DirectCommissionStatement.PayosPaymentLinkIdMaxLength).IsUnicode(false);
        builder.Property(s => s.PaidAmountVnd).HasColumnName("paid_amount_vnd").IsRequired();
        builder.Property(s => s.PaidAtUtc).HasColumnName("paid_at").AsUtcDateTime2();
        builder.Property(s => s.IssueDate).HasColumnName("issue_date").HasColumnType("date").IsRequired();
        builder.Property(s => s.DueDate).HasColumnName("due_date").HasColumnType("date").IsRequired();
        builder.Property(s => s.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(s => s.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.Property(s => s.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasOne<User>().WithMany()
            .HasForeignKey(s => s.TourOperatorUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.StatementCode).IsUnique()
            .HasDatabaseName("UQ_TourDirectCommissionStatements_Code");
        builder.HasIndex(s => new { s.TourOperatorUserId, s.Period }).IsUnique()
            .HasDatabaseName("UQ_TourDirectCommissionStatements_Operator_Period");
        builder.HasIndex(s => s.TourOperatorUserId)
            .HasDatabaseName("IX_TourDirectCommissionStatements_Operator");
        builder.HasIndex(s => new { s.Status, s.DueDate })
            .HasDatabaseName("IX_TourDirectCommissionStatements_Status");
    }
}