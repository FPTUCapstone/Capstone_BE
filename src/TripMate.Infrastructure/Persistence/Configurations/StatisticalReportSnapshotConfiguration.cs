using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class StatisticalReportSnapshotConfiguration : IEntityTypeConfiguration<StatisticalReportSnapshot>
{
    public void Configure(EntityTypeBuilder<StatisticalReportSnapshot> builder)
    {
        builder.ToTable("StatisticalReportSnapshots", "reporting");
        builder.HasKey(snapshot => snapshot.Id);
        builder.Property(snapshot => snapshot.Id).HasColumnName("snapshot_id");
        builder.Property(snapshot => snapshot.CriteriaKey).HasColumnName("criteria_key").HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.Property(snapshot => snapshot.PeriodKey).HasColumnName("period_key").HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(snapshot => snapshot.ReportType).HasColumnName("report_type").HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(snapshot => snapshot.BookingType).HasColumnName("booking_type").HasMaxLength(24).IsUnicode(false).IsRequired();
        builder.Property(snapshot => snapshot.OperatorUserId).HasColumnName("operator_user_id");
        builder.Property(snapshot => snapshot.DestinationId).HasColumnName("destination_id");
        builder.Property(snapshot => snapshot.PayloadJson).HasColumnName("payload_json").HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(snapshot => snapshot.GeneratedAtUtc).HasColumnName("generated_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(snapshot => snapshot.CriteriaKey).IsUnique().HasDatabaseName("UX_StatisticalReportSnapshots_CriteriaKey");
    }
}
