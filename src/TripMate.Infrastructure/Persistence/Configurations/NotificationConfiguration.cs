using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", "dbo");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("notification_id").ValueGeneratedOnAdd();

        builder.Property(n => n.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(n => n.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(10);
        builder.Property(n => n.Type).HasColumnName("type").HasMaxLength(40).IsRequired();
        builder.Property(n => n.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(n => n.Body).HasColumnName("body").HasMaxLength(1000);
        builder.Property(n => n.RelatedEntityType).HasColumnName("related_entity_type").HasMaxLength(40);
        builder.Property(n => n.RelatedEntityId).HasColumnName("related_entity_id");
        builder.Property(n => n.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10);
        builder.Property(n => n.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(n => n.SentAtUtc).HasColumnName("sent_at").AsUtcDateTime2();
        builder.Property(n => n.DeliveryAttemptCount)
            .HasColumnName("delivery_attempt_count")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(n => n.DeliveryLeaseToken).HasColumnName("delivery_lease_token");
        builder.Property(n => n.DeliveryLeaseExpiresAtUtc)
            .HasColumnName("delivery_lease_expires_at")
            .AsUtcDateTime2();
        builder.Property(n => n.NextDeliveryAttemptAtUtc)
            .HasColumnName("next_delivery_attempt_at")
            .AsUtcDateTime2();
        builder.Property(n => n.DeliveryLastErrorCode)
            .HasColumnName("delivery_last_error_code")
            .HasMaxLength(Notification.DeliveryLastErrorCodeMaxLength)
            .IsUnicode(false);

        builder.HasIndex(n => new { n.UserId, n.Status }).HasDatabaseName("IX_Notifications_User_Status");
        builder.HasIndex(n => new
        {
            n.Status,
            n.Channel,
            n.Type,
            n.NextDeliveryAttemptAtUtc,
            n.DeliveryLeaseExpiresAtUtc,
            n.Id,
        }).HasDatabaseName("IX_Notifications_Delivery_Due");

        builder
            .HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}