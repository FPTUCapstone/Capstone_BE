using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs", "dbo");

        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Id).HasColumnName("audit_log_id").ValueGeneratedOnAdd();
        builder.Property(audit => audit.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(audit => audit.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(audit => audit.AffectedEntity)
            .HasColumnName("affected_entity")
            .HasMaxLength(80)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(audit => audit.AffectedEntityId).HasColumnName("affected_entity_id");
        builder.Property(audit => audit.BeforeData).HasColumnName("before_data");
        builder.Property(audit => audit.AfterData).HasColumnName("after_data");
        builder.Property(audit => audit.IpAddress)
            .HasColumnName("ip_address")
            .HasMaxLength(45)
            .IsUnicode(false);
        builder.Property(audit => audit.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2();

        builder.HasOne(audit => audit.ActorUser)
            .WithMany()
            .HasForeignKey(audit => audit.ActorUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(audit => new { audit.AffectedEntity, audit.AffectedEntityId })
            .HasDatabaseName("IX_AuditLogs_Entity");
        builder.HasIndex(audit => new { audit.ActorUserId, audit.CreatedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_AuditLogs_Actor_Date");
    }
}