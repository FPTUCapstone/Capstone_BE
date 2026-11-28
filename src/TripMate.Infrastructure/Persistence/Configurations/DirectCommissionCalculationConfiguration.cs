using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectCommissionCalculationConfiguration : IEntityTypeConfiguration<DirectCommissionCalculation>
{
    public void Configure(EntityTypeBuilder<DirectCommissionCalculation> b)
    {
        b.ToTable("TourCommissionCalculations", "commercial");
        b.HasKey(c => c.Id); b.Property(c => c.Id).HasColumnName("calculation_id").ValueGeneratedOnAdd();
        b.Property(c => c.StatementId).HasColumnName("statement_id");
        b.Property(c => c.GrossRevenueVnd).HasColumnName("gross_revenue_vnd");
        b.Property(c => c.CompletedRefundsVnd).HasColumnName("completed_refunds_vnd");
        b.Property(c => c.RevenueCommissionVnd).HasColumnName("revenue_commission_vnd");
        b.Property(c => c.RefundCommissionCreditVnd).HasColumnName("refund_commission_credit_vnd");
        b.Property(c => c.CreditBroughtForwardVnd).HasColumnName("credit_brought_forward_vnd");
        b.Property(c => c.CreditAppliedVnd).HasColumnName("credit_applied_vnd");
        b.Property(c => c.CreditCarriedForwardVnd).HasColumnName("credit_carried_forward_vnd");
        b.Property(c => c.HeldBookingCount).HasColumnName("held_booking_count");
        b.Property(c => c.AmbiguousLegacyBookingCount).HasColumnName("ambiguous_legacy_booking_count");
        b.Property(c => c.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasIndex(c => c.StatementId).IsUnique().HasDatabaseName("UX_TourCommissionCalculations_Statement");
        b.HasOne(c => c.Statement).WithMany().HasForeignKey(c => c.StatementId).OnDelete(DeleteBehavior.Restrict);
    }
}