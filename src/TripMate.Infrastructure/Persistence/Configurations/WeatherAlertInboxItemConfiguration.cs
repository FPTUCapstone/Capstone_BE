using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class WeatherAlertInboxItemConfiguration : IEntityTypeConfiguration<WeatherAlertInboxItem>
{
    public void Configure(EntityTypeBuilder<WeatherAlertInboxItem> builder)
    {
        builder.ToTable("WeatherAlertInboxItems", "trip");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("inbox_id").ValueGeneratedOnAdd();
        builder.Property(item => item.Provider).HasColumnName("provider")
            .HasMaxLength(WeatherAlertInboxItem.ProviderMaxLength).IsUnicode(false).IsRequired();
        builder.Property(item => item.ProviderNotificationId).HasColumnName("provider_notification_id")
            .HasMaxLength(WeatherAlertInboxItem.ProviderNotificationIdMaxLength).IsUnicode(false);
        builder.Property(item => item.Payload).HasColumnName("payload").IsRequired();
        builder.Property(item => item.Status).HasColumnName("status")
            .HasMaxLength(WeatherAlertInboxItem.StatusMaxLength).IsUnicode(false).IsRequired();
        // Each lease raises the attempt; two workers that read the same row cannot both claim it.
        builder.Property(item => item.AttemptCount).HasColumnName("attempt_count").IsRequired().IsConcurrencyToken();
        builder.Property(item => item.MaxAttempts).HasColumnName("max_attempts").IsRequired();
        builder.Property(item => item.LeaseExpiresAtUtc).HasColumnName("lease_expires_at").AsUtcDateTime2();
        builder.Property(item => item.LeasedBy).HasColumnName("leased_by")
            .HasMaxLength(WeatherAlertInboxItem.LeasedByMaxLength).IsUnicode(false);
        builder.Property(item => item.LastError).HasColumnName("last_error")
            .HasMaxLength(WeatherAlertInboxItem.LastErrorMaxLength);
        builder.Property(item => item.ReceivedAtUtc).HasColumnName("received_at").AsUtcDateTime2().IsRequired();
        builder.Property(item => item.ProcessedAtUtc).HasColumnName("processed_at").AsUtcDateTime2();

        builder.HasIndex(item => new { item.Provider, item.ProviderNotificationId }).IsUnique()
            .HasFilter("[provider_notification_id] IS NOT NULL")
            .HasDatabaseName("UX_WeatherAlertInboxItems_ProviderNotification");

        builder.HasIndex(item => new { item.Status, item.LeaseExpiresAtUtc })
            .HasDatabaseName("IX_WeatherAlertInboxItems_StatusLease");
    }
}
