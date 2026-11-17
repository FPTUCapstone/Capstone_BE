using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PostTripComplaintNegotiationEventConfiguration
    : IEntityTypeConfiguration<PostTripComplaintNegotiationEvent>
{
    public void Configure(EntityTypeBuilder<PostTripComplaintNegotiationEvent> builder)
    {
        builder.ToTable("PostTripComplaintNegotiationEvents", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("negotiation_event_id").ValueGeneratedOnAdd();
        builder.Property(item => item.ComplaintId).HasColumnName("complaint_id").IsRequired();
        builder.Property(item => item.Round).HasColumnName("round_number").IsRequired();
        builder.Property(item => item.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(item => item.Actor).HasColumnName("actor").HasConversion<string>().HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(item => item.EventType).HasColumnName("event_type").HasConversion<string>().HasMaxLength(24).IsUnicode(false).IsRequired();
        builder.Property(item => item.Amount).HasColumnName("amount").HasPrecision(12, 2);
        builder.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(PostTripComplaintNegotiationEvent.ReasonMaxLength);
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(item => new { item.ComplaintId, item.Id }).HasDatabaseName("IX_PostTripComplaintNegotiationEvents_Complaint");
        builder.HasOne<PostTripComplaint>().WithMany().HasForeignKey(item => item.ComplaintId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
