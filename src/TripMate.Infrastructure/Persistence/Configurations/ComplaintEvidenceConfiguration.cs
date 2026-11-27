using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ComplaintEvidenceConfiguration : IEntityTypeConfiguration<ComplaintEvidence>
{
    public void Configure(EntityTypeBuilder<ComplaintEvidence> builder)
    {
        builder.ToTable("ComplaintEvidence", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("evidence_id").ValueGeneratedOnAdd();
        builder.Property(item => item.ComplaintId).HasColumnName("complaint_id").IsRequired();
        builder.Property(item => item.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(ComplaintEvidence.OriginalFileNameMaxLength).IsRequired();
        builder.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(ComplaintEvidence.ContentTypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.ByteLength).HasColumnName("byte_length").IsRequired();
        builder.Property(item => item.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(ComplaintEvidence.StorageObjectKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.Checksum).HasColumnName("checksum").HasMaxLength(ComplaintEvidence.ChecksumMaxLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.StaffReviewEventId).HasColumnName("staff_review_event_id");
        builder.HasOne(item => item.StaffReviewEvent).WithMany().HasForeignKey(item => item.StaffReviewEventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => item.ComplaintId).HasDatabaseName("IX_ComplaintEvidence_Complaint");
        builder.HasIndex(item => item.StorageObjectKey).IsUnique().HasDatabaseName("UX_ComplaintEvidence_StorageObjectKey");
        builder.HasOne<PostTripComplaint>().WithMany().HasForeignKey(item => item.ComplaintId).OnDelete(DeleteBehavior.Cascade);
    }
}
