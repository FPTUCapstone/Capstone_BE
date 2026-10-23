using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ClosedStatisticalPeriodConfiguration : IEntityTypeConfiguration<ClosedStatisticalPeriod>
{
    public void Configure(EntityTypeBuilder<ClosedStatisticalPeriod> builder)
    {
        builder.ToTable("ClosedStatisticalPeriods", "reporting");
        builder.HasKey(period => period.Id);
        builder.Property(period => period.Id).HasColumnName("closed_period_id");
        builder.Property(period => period.PeriodKey).HasColumnName("period_key").HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(period => period.StartDate).HasColumnName("start_date").IsRequired();
        builder.Property(period => period.EndDate).HasColumnName("end_date").IsRequired();
        builder.Property(period => period.ClosedAtUtc).HasColumnName("closed_at").AsUtcDateTime2().IsRequired();
        builder.Property(period => period.ClosedByUserId).HasColumnName("closed_by_user_id");
        builder.HasIndex(period => period.PeriodKey).IsUnique().HasDatabaseName("UX_ClosedStatisticalPeriods_PeriodKey");
    }
}
