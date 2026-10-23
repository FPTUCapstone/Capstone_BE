using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class StatisticalReportExportOperationConfiguration : IEntityTypeConfiguration<StatisticalReportExportOperation>
{
    public void Configure(EntityTypeBuilder<StatisticalReportExportOperation> builder)
    {
        builder.ToTable("StatisticalReportExportOperations", "reporting");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("export_operation_id");
        builder.Property(item => item.OperationId).HasColumnName("operation_id").IsRequired();
        builder.Property(item => item.CriteriaKey).HasColumnName("criteria_key").HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.Property(item => item.Format).HasColumnName("format").HasMaxLength(8).IsUnicode(false).IsRequired();
        builder.Property(item => item.RequestedByUserId).HasColumnName("requested_by_user_id").IsRequired();
        builder.Property(item => item.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(item => item.OperationId).IsUnique().HasDatabaseName("UX_StatisticalReportExportOperations_OperationId");
    }
}
