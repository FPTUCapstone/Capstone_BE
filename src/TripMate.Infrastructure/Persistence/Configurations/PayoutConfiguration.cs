using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> builder)
    {
        builder.ToTable("Payouts", "payment");
        builder.HasKey(payout => payout.Id);
        builder.Property(payout => payout.Id).HasColumnName("payout_id").ValueGeneratedOnAdd();
        builder.Property(payout => payout.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(payout => payout.PeriodStart).HasColumnName("period_start").IsRequired();
        builder.Property(payout => payout.PeriodEnd).HasColumnName("period_end").IsRequired();
        builder.Property(payout => payout.GrossRevenue).HasColumnName("gross_revenue").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.CommissionAmount).HasColumnName("commission_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.NetAmount).HasColumnName("net_amount").HasPrecision(14, 2).IsRequired();
        builder.Property(payout => payout.Status).HasColumnName("status").HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(payout => payout.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2();
        builder.Property(payout => payout.ConfirmedBy).HasColumnName("confirmed_by");
        builder.Property(payout => payout.ConfirmedAtUtc).HasColumnName("confirmed_at").AsUtcDateTime2();
        builder.HasOne(payout => payout.Operator).WithMany()
            .HasForeignKey(payout => payout.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(payout => new { payout.OperatorUserId, payout.Status })
            .HasDatabaseName("IX_Payouts_Operator");
    }
}