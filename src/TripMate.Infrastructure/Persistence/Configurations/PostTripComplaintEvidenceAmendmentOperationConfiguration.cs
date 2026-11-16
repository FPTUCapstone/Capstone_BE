using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PostTripComplaintEvidenceAmendmentOperationConfiguration : IEntityTypeConfiguration<PostTripComplaintEvidenceAmendmentOperation>
{
    public void Configure(EntityTypeBuilder<PostTripComplaintEvidenceAmendmentOperation> builder)
    {
        builder.ToTable("PostTripComplaintEvidenceAmendmentOperations", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("evidence_amendment_operation_id").ValueGeneratedOnAdd();
        builder.Property(item => item.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(item => item.PayloadFingerprint).HasColumnName("payload_fingerprint").HasMaxLength(PostTripComplaintEvidenceAmendmentOperation.PayloadFingerprintLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.ComplaintId).HasColumnName("complaint_id").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(item => new { item.TravelerUserId, item.IdempotencyKey }).HasDatabaseName("UQ_PostTripComplaintEvidenceAmendmentOperations_TravelerKey").IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.TravelerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PostTripComplaint>().WithMany().HasForeignKey(item => item.ComplaintId).OnDelete(DeleteBehavior.Restrict);
    }
}
