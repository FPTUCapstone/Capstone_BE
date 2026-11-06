using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class WeatherSafetyReviewConfiguration : IEntityTypeConfiguration<WeatherSafetyReview>
{
    public void Configure(EntityTypeBuilder<WeatherSafetyReview> builder)
    {
        builder.ToTable("WeatherSafetyReviews", "trip");
        builder.HasKey(review => review.Id);
        builder.Property(review => review.Id).HasColumnName("review_id").ValueGeneratedOnAdd();
        builder.Property(review => review.TourScheduleId).HasColumnName("tour_schedule_id").IsRequired();
        builder.Property(review => review.Provider).HasColumnName("provider")
            .HasMaxLength(WeatherSafetyReview.ProviderMaxLength).IsUnicode(false).IsRequired();
        builder.Property(review => review.ProviderEventId).HasColumnName("provider_event_id")
            .HasMaxLength(WeatherSafetyReview.ProviderEventIdMaxLength).IsUnicode(false).IsRequired();
        builder.Property(review => review.Status).HasColumnName("status")
            .HasMaxLength(WeatherSafetyReview.StatusMaxLength).IsUnicode(false).IsRequired();
        builder.Property(review => review.UrgentNoticeSentAtUtc).HasColumnName("urgent_notice_sent_at").AsUtcDateTime2().IsRequired();
        builder.Property(review => review.AcknowledgementDeadlineUtc).HasColumnName("acknowledgement_deadline").AsUtcDateTime2().IsRequired();
        builder.Property(review => review.AcknowledgedAtUtc).HasColumnName("acknowledged_at").AsUtcDateTime2();
        builder.Property(review => review.AcknowledgedByOperatorId).HasColumnName("acknowledged_by_operator_id");
        builder.Property(review => review.EscalatedAtUtc).HasColumnName("escalated_at").AsUtcDateTime2();
        builder.Property(review => review.EscalationContact).HasColumnName("escalation_contact")
            .HasMaxLength(WeatherSafetyReview.EscalationContactMaxLength);
        builder.Property(review => review.ImmediateInstructions).HasColumnName("immediate_instructions")
            .HasMaxLength(WeatherSafetyReview.InstructionsMaxLength);
        builder.Property(review => review.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();

        builder.HasOne(review => review.TourSchedule).WithMany()
            .HasForeignKey(review => review.TourScheduleId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(review => review.AcknowledgedByOperatorId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);

        // Escalation and acknowledgement race on this: the later save must not overwrite.
        builder.Property(review => review.Status).IsConcurrencyToken();

        builder.HasIndex(review => new { review.TourScheduleId, review.Provider, review.ProviderEventId }).IsUnique()
            .HasDatabaseName("UX_WeatherSafetyReviews_ScheduleEvent");

        // The escalation sweep: status = PendingAcknowledgement AND acknowledgement_deadline <= now.
        builder.HasIndex(review => new { review.Status, review.AcknowledgementDeadlineUtc })
            .HasDatabaseName("IX_WeatherSafetyReviews_StatusDeadline");
    }
}
