using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class OperatorContractTemplateConfiguration : IEntityTypeConfiguration<OperatorContractTemplate>
{
    public void Configure(EntityTypeBuilder<OperatorContractTemplate> builder)
    {
        builder.ToTable("OperatorContractTemplates", "admin");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("template_id").ValueGeneratedOnAdd();
        builder.Property(x => x.TemplateType).HasColumnName("template_type").HasMaxLength(40).IsUnicode(false);
        builder.Property(x => x.Language).HasColumnName("language").HasMaxLength(10).IsUnicode(false);
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsUnicode(true);
        builder.Property(x => x.SourceStorageKey).HasColumnName("source_storage_key").HasMaxLength(500).IsUnicode(true);
        builder.Property(x => x.SourceSha256).HasColumnName("source_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.SourceFileName).HasColumnName("source_file_name").HasMaxLength(255).IsUnicode(true);
        builder.Property(x => x.VariablesManifestJson).HasColumnName("variables_manifest_json").IsUnicode(true);
        builder.Property(x => x.PreviewStorageKey).HasColumnName("preview_storage_key").HasMaxLength(500).IsUnicode(true);
        builder.Property(x => x.PreviewSha256).HasColumnName("preview_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.RenderErrorCode).HasColumnName("render_error_code").HasMaxLength(100).IsUnicode(false);
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.DraftRevision).HasColumnName("draft_revision");
        builder.Property(x => x.ValidationValid).HasColumnName("validation_valid");
        builder.Property(x => x.PreviewRevision).HasColumnName("preview_revision");
        builder.Property(x => x.PreviewAcknowledgedRevision).HasColumnName("preview_acknowledged_revision");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(x => x.PublishedByUserId).HasColumnName("published_by_user_id");
        builder.Property(x => x.PublicationIdempotencyKey).HasColumnName("publication_idempotency_key");
        builder.Property(x => x.PublicationExpectedGeneration).HasColumnName("publication_expected_generation");
        builder.Property(x => x.PublicationExpectedCurrentId).HasColumnName("publication_expected_current_id");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();
        builder.Property(x => x.PublishedAtUtc).HasColumnName("published_at").AsUtcDateTime2();
        builder.Property(x => x.ArchivedAtUtc).HasColumnName("archived_at").AsUtcDateTime2();
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(12).IsUnicode(false);
        builder.Property(x => x.RenderState).HasColumnName("render_state").HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.HasIndex(x => new { x.TemplateType, x.Language }).IsUnique().HasFilter("[status] = 'Published'").HasDatabaseName("UX_OperatorContractTemplates_Published");
        builder.HasIndex(x => new { x.TemplateType, x.Language, x.Version }).IsUnique().HasFilter("[version] IS NOT NULL").HasDatabaseName("UX_OperatorContractTemplates_Version");
        builder.HasIndex(x => x.PublicationIdempotencyKey).IsUnique().HasFilter("[publication_idempotency_key] IS NOT NULL").HasDatabaseName("UX_OperatorContractTemplates_PublicationKey");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.PublishedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}