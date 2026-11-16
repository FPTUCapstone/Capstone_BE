using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PostTripComplaintConfiguration : IEntityTypeConfiguration<PostTripComplaint>
{
    public void Configure(EntityTypeBuilder<PostTripComplaint> builder)
    {
        builder.ToTable("PostTripComplaints", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("complaint_id").ValueGeneratedOnAdd();
        builder.Property(item => item.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(item => item.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(item => item.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(item => item.ReasonCategory).HasColumnName("reason_category").HasConversion<string>().HasMaxLength(40).IsUnicode(false).IsRequired();
        builder.Property(item => item.ReasonDetail).HasColumnName("reason_detail").HasMaxLength(PostTripComplaint.ReasonDetailMaxLength).IsRequired();
        builder.Property(item => item.SubmittedAtUtc).HasColumnName("submitted_at").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.ResponseDeadlineUtc).HasColumnName("response_deadline_at").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.TravelerResponseDeadlineUtc).HasColumnName("traveler_response_deadline_at").AsUtcDateTime2();
        builder.Property(item => item.ResponseAction).HasColumnName("response_action").HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        builder.Property(item => item.ProposedRefundAmount).HasColumnName("proposed_refund_amount").HasPrecision(12, 2);
        builder.Property(item => item.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(PostTripComplaint.ReasonDetailMaxLength);
        builder.Property(item => item.RespondedAtUtc).HasColumnName("responded_at").AsUtcDateTime2();
        builder.Property(item => item.RespondedByOperatorUserId).HasColumnName("responded_by_operator_user_id");
        builder.Property(item => item.RefundableAmount).HasColumnName("refundable_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(item => item.RefundId).HasColumnName("refund_id");
        builder.Property(item => item.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.HasIndex(item => item.BookingId).IsUnique().HasDatabaseName("UX_PostTripComplaints_Booking");
        builder.HasIndex(item => new { item.Status, item.ResponseDeadlineUtc }).HasDatabaseName("IX_PostTripComplaints_Deadline");
        builder.HasOne<Booking>().WithMany().HasForeignKey(item => item.BookingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.TravelerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Refund>().WithMany().HasForeignKey(item => item.RefundId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.RespondedByOperatorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
