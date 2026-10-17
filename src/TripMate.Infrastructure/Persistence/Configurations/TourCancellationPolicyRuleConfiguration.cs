using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourCancellationPolicyRuleConfiguration : IEntityTypeConfiguration<TourCancellationPolicyRule>
{
    public void Configure(EntityTypeBuilder<TourCancellationPolicyRule> builder)
    {
        builder.ToTable("TourCancellationPolicyRules", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("tour_cancellation_policy_rule_id").ValueGeneratedOnAdd();
        builder.Property(item => item.TourId).HasColumnName("tour_id");
        builder.Property(item => item.DaysBeforeDeparture).HasColumnName("days_before_departure");
        builder.Property(item => item.CancellationFeePercent).HasColumnName("cancellation_fee_percent").HasPrecision(5, 2);
        builder.HasIndex(item => new { item.TourId, item.DaysBeforeDeparture })
            .HasDatabaseName("UQ_TourCancellationPolicyRules_Cutoff").IsUnique();
    }
}