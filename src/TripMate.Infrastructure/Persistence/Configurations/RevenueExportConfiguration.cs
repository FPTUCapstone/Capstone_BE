using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>Maps reporting.RevenueExports from the UC-45 migration.</summary>
public sealed class RevenueExportConfiguration : IEntityTypeConfiguration<RevenueExport>
{
    public void Configure(EntityTypeBuilder<RevenueExport> builder)
    {
        builder.ToTable("RevenueExports", "reporting");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("export_id").ValueGeneratedOnAdd();

        builder.Property(x => x.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(x => x.StartDate).HasColumnName("start_date").IsRequired();
        builder.Property(x => x.EndDate).HasColumnName("end_date").IsRequired();
        builder.Property(x => x.Granularity).HasColumnName("granularity").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(x => x.TourId).HasColumnName("tour_id");

        builder.Property(x => x.Format).HasColumnName("format").HasMaxLength(10).HasConversion<string>().IsRequired();
        builder.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(20).HasConversion<string>().IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(12).HasConversion<string>().IsRequired();

        builder.Property(x => x.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2().IsRequired();
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at").AsUtcDateTime2();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2();

        builder.Property(x => x.RowCount).HasColumnName("row_count");
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(500);
        builder.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(100);

        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasOne<TripMate.Domain.Entities.User>()
            .WithMany()
            .HasForeignKey(x => x.OperatorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tour>()
            .WithMany()
            .HasForeignKey(x => x.TourId)
            .OnDelete(DeleteBehavior.Restrict);

        // Operator history listing (BR-105: scoped to caller), newest first.
        builder.HasIndex(x => new { x.OperatorUserId, x.RequestedAtUtc })
            .HasDatabaseName("IX_RevenueExports_Operator");

        // Worker claim scan: only Queued rows are candidates.
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_RevenueExports_Status_Queued")
            .HasFilter("[status] = 'Queued'");

        // Expiry cleanup scan: only Ready rows with an expiry are candidates.
        builder.HasIndex(x => x.ExpiresAtUtc)
            .HasDatabaseName("IX_RevenueExports_Expiry_Ready")
            .HasFilter("[status] = 'Ready'");
    }
}
