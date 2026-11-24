using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ContractTemplatePublicationHeadConfiguration : IEntityTypeConfiguration<ContractTemplatePublicationHead>
{
    public void Configure(EntityTypeBuilder<ContractTemplatePublicationHead> builder)
    {
        builder.ToTable("ContractTemplatePublicationHeads", "admin");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("head_id").ValueGeneratedOnAdd();
        builder.Property(x => x.TemplateType).HasColumnName("template_type").HasMaxLength(40).IsUnicode(false);
        builder.Property(x => x.Language).HasColumnName("language").HasMaxLength(10).IsUnicode(false);
        builder.Property(x => x.CurrentPublishedTemplateId).HasColumnName("current_published_template_id");
        builder.Property(x => x.CurrentVersion).HasColumnName("current_version");
        builder.Property(x => x.Generation).HasColumnName("generation");
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => new { x.TemplateType, x.Language }).IsUnique().HasDatabaseName("UX_ContractTemplatePublicationHeads_TypeLanguage");
        builder.HasOne<OperatorContractTemplate>().WithMany().HasForeignKey(x => x.CurrentPublishedTemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}