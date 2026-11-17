using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PostTripComplaintResponseOperationConfiguration
    : IEntityTypeConfiguration<PostTripComplaintResponseOperation>
{
    public void Configure(EntityTypeBuilder<PostTripComplaintResponseOperation> builder)
    {
        builder.ToTable("PostTripComplaintResponseOperations", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("response_operation_id").ValueGeneratedOnAdd();
        builder.Property(item => item.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(item => item.PayloadFingerprint).HasColumnName("payload_fingerprint")
            .HasMaxLength(PostTripComplaintResponseOperation.PayloadFingerprintLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.ComplaintId).HasColumnName("complaint_id").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(item => new { item.OperatorUserId, item.IdempotencyKey }).IsUnique()
            .HasDatabaseName("UQ_PostTripComplaintResponseOperations_OperatorKey");
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PostTripComplaint>().WithMany().HasForeignKey(item => item.ComplaintId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
