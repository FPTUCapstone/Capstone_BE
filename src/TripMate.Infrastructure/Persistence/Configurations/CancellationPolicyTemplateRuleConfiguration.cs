using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class CancellationPolicyTemplateRuleConfiguration : IEntityTypeConfiguration<CancellationPolicyTemplateRule>
{
    public void Configure(EntityTypeBuilder<CancellationPolicyTemplateRule> builder)
    {
        builder.ToTable("CancellationPolicyTemplateRules", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("cancellation_policy_template_rule_id").ValueGeneratedOnAdd();
        builder.Property(item => item.CancellationPolicyTemplateId).HasColumnName("cancellation_policy_template_id");
        builder.Property(item => item.DaysBeforeDeparture).HasColumnName("days_before_departure");
        builder.Property(item => item.CancellationFeePercent).HasColumnName("cancellation_fee_percent").HasPrecision(5, 2);
        builder.HasIndex(item => new { item.CancellationPolicyTemplateId, item.DaysBeforeDeparture })
            .HasDatabaseName("UQ_CancellationPolicyTemplateRules_Cutoff").IsUnique();
    }
}