using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectRefundEvidenceConfiguration : IEntityTypeConfiguration<DirectRefundEvidence>
{
    public void Configure(EntityTypeBuilder<DirectRefundEvidence> builder)
    {
        builder.ToTable("DirectRefundEvidences", "payment");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("evidence_id").ValueGeneratedOnAdd();
        builder.Property(e => e.CaseId).HasColumnName("case_id").IsRequired();
        builder.Property(e => e.OriginalFileName).HasColumnName("original_file_name")
            .HasMaxLength(DirectRefundEvidence.OriginalFileNameMaxLength).IsRequired();
        builder.Property(e => e.ContentType).HasColumnName("content_type")
            .HasMaxLength(DirectRefundEvidence.ContentTypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.ByteLength).HasColumnName("byte_length").IsRequired();
        builder.Property(e => e.StorageObjectKey).HasColumnName("storage_object_key")
            .HasMaxLength(DirectRefundEvidence.StorageObjectKeyMaxLength).IsRequired();
        builder.Property(e => e.Checksum).HasColumnName("checksum")
            .HasMaxLength(DirectRefundEvidence.ChecksumMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.UploadedAt).HasColumnName("uploaded_at").AsUtcDateTime2().IsRequired();

        builder.HasOne<DirectRefundCase>().WithMany()
            .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.CaseId).HasDatabaseName("IX_DirectRefundEvidences_Case");
    }
}
