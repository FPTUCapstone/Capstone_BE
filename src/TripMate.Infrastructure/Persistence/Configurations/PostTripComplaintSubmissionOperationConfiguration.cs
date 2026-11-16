using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PostTripComplaintSubmissionOperationConfiguration
    : IEntityTypeConfiguration<PostTripComplaintSubmissionOperation>
{
    public void Configure(EntityTypeBuilder<PostTripComplaintSubmissionOperation> builder)
    {
        builder.ToTable("PostTripComplaintSubmissionOperations", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("submission_operation_id").ValueGeneratedOnAdd();
        builder.Property(item => item.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(item => item.PayloadFingerprint).HasColumnName("payload_fingerprint")
            .HasMaxLength(PostTripComplaintSubmissionOperation.PayloadFingerprintLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.ComplaintId).HasColumnName("complaint_id").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.TravelerUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(item => item.Complaint).WithMany().HasForeignKey(item => item.ComplaintId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(item => new { item.TravelerUserId, item.IdempotencyKey })
            .HasDatabaseName("UQ_PostTripComplaintSubmissionOperations_TravelerKey").IsUnique();
    }
}