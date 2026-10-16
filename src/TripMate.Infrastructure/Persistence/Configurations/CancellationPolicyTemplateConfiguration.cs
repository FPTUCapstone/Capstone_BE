using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class CancellationPolicyTemplateConfiguration : IEntityTypeConfiguration<CancellationPolicyTemplate>
{
    public void Configure(EntityTypeBuilder<CancellationPolicyTemplate> builder)
    {
        builder.ToTable("CancellationPolicyTemplates", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("cancellation_policy_template_id").ValueGeneratedOnAdd();
        builder.Property(item => item.Name).HasColumnName("name").HasMaxLength(CancellationPolicyTemplate.NameMaxLength).IsRequired();
        builder.Property(item => item.Description).HasColumnName("description").HasMaxLength(CancellationPolicyTemplate.DescriptionMaxLength);
        builder.Property(item => item.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.HasMany(item => item.Rules).WithOne(rule => rule.Template)
            .HasForeignKey(rule => rule.CancellationPolicyTemplateId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(item => item.Rules).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(item => item.Name).HasDatabaseName("UX_CancellationPolicyTemplates_Name").IsUnique();
    }
}