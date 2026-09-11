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

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("audit_log_id").ValueGeneratedOnAdd();

        builder.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(a => a.ActionType).HasColumnName("action_type").HasMaxLength(50).IsRequired();
        builder.Property(a => a.AffectedEntity).HasColumnName("affected_entity").HasMaxLength(80).IsRequired();
        builder.Property(a => a.AffectedEntityId).HasColumnName("affected_entity_id");
        builder.Property(a => a.BeforeData).HasColumnName("before_data");
        builder.Property(a => a.AfterData).HasColumnName("after_data");
        builder.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();

        builder.HasIndex(a => new { a.AffectedEntity, a.AffectedEntityId }).HasDatabaseName("IX_AuditLogs_Entity");

        builder
            .HasOne(a => a.ActorUser)
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
