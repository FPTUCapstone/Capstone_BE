using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class AlertDeliveryWorkConfiguration : IEntityTypeConfiguration<AlertDeliveryWork>
{
    public void Configure(EntityTypeBuilder<AlertDeliveryWork> builder)
    {
        builder.ToTable("AlertDeliveryWork", "trip");
        builder.HasKey(work => work.NotificationId);
        builder.Property(work => work.NotificationId).HasColumnName("notification_id").ValueGeneratedNever();
        builder.Property(work => work.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(work => work.NextAttemptAtUtc).HasColumnName("next_attempt_at").AsUtcDateTime2();
        builder.Property(work => work.ClaimToken).HasColumnName("claim_token");
        builder.Property(work => work.ClaimExpiresAtUtc).HasColumnName("claim_expires_at").AsUtcDateTime2();
        builder.HasOne(work => work.Notification)
            .WithOne()
            .HasForeignKey<AlertDeliveryWork>(work => work.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(work => new { work.NextAttemptAtUtc, work.ClaimExpiresAtUtc })
            .HasDatabaseName("IX_AlertDeliveryWork_Due");
    }
}