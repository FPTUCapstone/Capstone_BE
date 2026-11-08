using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutRunConfiguration : IEntityTypeConfiguration<PayoutRun>
{
    public void Configure(EntityTypeBuilder<PayoutRun> builder)
    {
        builder.ToTable("PayoutRun", "payment");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(run => run.RunDate).HasColumnName("run_date").IsRequired();
        builder.Property(run => run.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
        builder.Property(run => run.StartedAtUtc).HasColumnName("started_at_utc").AsUtcDateTime2().IsRequired();
        builder.Property(run => run.CompletedAtUtc).HasColumnName("completed_at_utc").AsUtcDateTime2();
        builder.Property(run => run.CreatedAtUtc).HasColumnName("created_at_utc").AsUtcDateTime2().IsRequired();

        builder.HasIndex(run => run.RunDate).IsUnique().HasDatabaseName("UX_PayoutRun_RunDate");
    }
}
