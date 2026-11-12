using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourOperatorDebtConfiguration : IEntityTypeConfiguration<TourOperatorDebt>
{
    public void Configure(EntityTypeBuilder<TourOperatorDebt> builder)
    {
        builder.ToTable("TourOperatorDebt", "payment");
        builder.HasKey(debt => debt.Id);
        builder.Property(debt => debt.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(debt => debt.TourOperatorUserId).HasColumnName("tour_operator_user_id").IsRequired();
        builder.Property(debt => debt.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        builder.Property(debt => debt.RecoveredAmount).HasColumnName("recovered_amount").HasPrecision(18, 2).IsRequired();
        builder.Property(debt => debt.IncurredAtUtc).HasColumnName("incurred_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(debt => debt.CreatedAtUtc).HasColumnName("created_at_utc").AsUtcDateTime2().IsRequired();
        builder.Ignore(debt => debt.OutstandingAmount);

        builder.HasOne<User>().WithMany().HasForeignKey(debt => debt.TourOperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(debt => new { debt.TourOperatorUserId, debt.IncurredAtUtc })
            .HasDatabaseName("IX_TourOperatorDebt_Operator_Incurred");
    }
}