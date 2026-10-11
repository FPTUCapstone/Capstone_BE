using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripLocationLogConfiguration : IEntityTypeConfiguration<TripLocationLog>
{
    public void Configure(EntityTypeBuilder<TripLocationLog> builder)
    {
        builder.ToTable("TripLocationLogs", "trip");
        builder.HasKey(log => log.Id);
        builder.Property(log => log.Id).HasColumnName("log_id").ValueGeneratedOnAdd();
        builder.Property(log => log.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(log => log.SubjectUserId).HasColumnName("subject_user_id");
        builder.Property(log => log.Latitude).HasColumnName("latitude").HasPrecision(9, 6).IsRequired();
        builder.Property(log => log.Longitude).HasColumnName("longitude").HasPrecision(9, 6).IsRequired();
        builder.Property(log => log.RecordedAtUtc).HasColumnName("recorded_at").AsUtcDateTime2().IsRequired();
        builder.Property(log => log.SyncedAtUtc).HasColumnName("synced_at").AsUtcDateTime2();
        builder.Property(log => log.IsOfflineCaptured).HasColumnName("is_offline_captured").IsRequired();
        builder.HasOne(log => log.Session).WithMany().HasForeignKey(log => log.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(log => log.SubjectUser).WithMany().HasForeignKey(log => log.SubjectUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(log => new { log.SessionId, log.SubjectUserId, log.RecordedAtUtc })
            .HasDatabaseName("IX_TripLocationLogs_SessionSubjectRecorded");
    }
}
