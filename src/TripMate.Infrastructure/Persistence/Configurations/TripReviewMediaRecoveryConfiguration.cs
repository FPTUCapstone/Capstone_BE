using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripReviewMediaRecoveryConfiguration : IEntityTypeConfiguration<TripReviewMediaRecovery>
{
    public void Configure(EntityTypeBuilder<TripReviewMediaRecovery> b)
    {
        b.ToTable("TripReviewMediaRecovery", "social"); b.HasKey(x => x.OperationId);
        b.Property(x => x.OperationId).HasColumnName("operation_id").ValueGeneratedNever();
        b.HasOne<TripReviewMediaOperation>().WithOne().HasForeignKey<TripReviewMediaRecovery>(x => x.OperationId).OnDelete(DeleteBehavior.NoAction);
        b.Property(x => x.UploadFence).HasColumnName("upload_fence");
        b.Property(x => x.UploadLeaseUntilUtc).HasColumnName("upload_lease_until").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.UploadOutcome).HasColumnName("upload_outcome").HasMaxLength(16).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.CleanupFence).HasColumnName("cleanup_fence");
        b.Property(x => x.CleanupLeaseUntilUtc).HasColumnName("cleanup_lease_until").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.Exhausted).HasColumnName("exhausted").HasDefaultValue(false);
        b.Property(x => x.LastFailureCode).HasColumnName("last_failure_code").HasMaxLength(24).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.LastFailureAtUtc).HasColumnName("last_failure_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.Version).HasColumnName("version").IsRowVersion();
        b.Ignore(x => x.HasTerminalUploadEvidence);
        b.HasIndex(x => new { x.Exhausted, x.NextAttemptAtUtc, x.OperationId }).HasDatabaseName("IX_TripReviewMediaRecovery_Due");
    }
}