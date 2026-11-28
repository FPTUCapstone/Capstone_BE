using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ComplaintStaffReviewEventConfiguration : IEntityTypeConfiguration<ComplaintStaffReviewEvent>
{
    public void Configure(EntityTypeBuilder<ComplaintStaffReviewEvent> builder)
    {
        builder.ToTable("ComplaintStaffReviewEvents", "commerce");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("review_event_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ComplaintId).HasColumnName("complaint_id");
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.OperationId).HasColumnName("operation_id");
        builder.Property(x => x.PayloadFingerprint).HasColumnName("payload_fingerprint").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(32).IsUnicode(false);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(ComplaintStaffReviewEvent.TextMaxLength);
        builder.Property(x => x.ContractBasis).HasColumnName("contract_basis").HasMaxLength(ComplaintStaffReviewEvent.TextMaxLength);
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
        builder.Property(x => x.TargetParty).HasColumnName("target_party").HasMaxLength(16).IsUnicode(false);
        builder.Property(x => x.RequestEventId).HasColumnName("request_event_id");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(x => x.DueAtUtc).HasColumnName("due_at").AsUtcDateTime2();
        builder.HasIndex(x => new { x.ActorUserId, x.OperationId }).IsUnique().HasDatabaseName("UX_ComplaintStaffReviewEvents_Operation");
        builder.HasIndex(x => new { x.ComplaintId, x.Id }).HasDatabaseName("IX_ComplaintStaffReviewEvents_Complaint");
        builder.HasOne<PostTripComplaint>().WithMany().HasForeignKey(x => x.ComplaintId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}