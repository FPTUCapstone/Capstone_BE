using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripStateHistoryConfiguration : IEntityTypeConfiguration<TripStateHistory>
{
    public void Configure(EntityTypeBuilder<TripStateHistory> builder)
    {
        builder.ToTable("TripStateHistory", "trip");
        builder.HasKey(history => history.Id);
        builder.Property(history => history.Id).HasColumnName("history_id").ValueGeneratedOnAdd();
        builder.Property(history => history.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(history => history.FromState).HasColumnName("from_state").HasMaxLength(12).IsUnicode(false);
        builder.Property(history => history.ToState).HasColumnName("to_state").HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(history => history.Reason).HasColumnName("reason").HasMaxLength(500);
        builder.Property(history => history.TriggeredBy).HasColumnName("triggered_by").HasMaxLength(20).IsUnicode(false);
        builder.Property(history => history.ChangedAtUtc).HasColumnName("changed_at").AsUtcDateTime2().IsRequired();
        builder.HasOne(history => history.Session).WithMany().HasForeignKey(history => history.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}