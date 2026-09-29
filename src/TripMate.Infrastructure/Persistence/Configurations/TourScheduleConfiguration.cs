using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps commerce.TourSchedules from database/tripmate_schema_v7.sql.
/// </summary>
public class TourScheduleConfiguration : IEntityTypeConfiguration<TourSchedule>
{
    public void Configure(EntityTypeBuilder<TourSchedule> builder)
    {
        builder.ToTable("TourSchedules", "commerce");

        builder.HasKey(schedule => schedule.Id);
        builder.Property(schedule => schedule.Id)
            .HasColumnName("schedule_id")
            .ValueGeneratedOnAdd();
        builder.Property(schedule => schedule.TourId)
            .HasColumnName("tour_id")
            .IsRequired();
        builder.Property(schedule => schedule.StartAtUtc)
            .HasColumnName("start_datetime")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(schedule => schedule.EndAtUtc)
            .HasColumnName("end_datetime")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(schedule => schedule.MeetingPoint)
            .HasColumnName("meeting_point")
            .HasMaxLength(TourSchedule.MeetingPointMaxLength);
        builder.Property(schedule => schedule.TotalCapacity)
            .HasColumnName("total_capacity")
            .IsRequired();
        builder.Property(schedule => schedule.ReservedCapacity)
            .HasColumnName("reserved_capacity")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(schedule => schedule.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(12)
            .IsUnicode(false)
            .HasDefaultValueSql("'Scheduled'")
            .IsRequired();
        builder.Property(schedule => schedule.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(schedule => schedule.Tour)
            .WithMany(tour => tour.Schedules)
            .HasForeignKey(schedule => schedule.TourId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(schedule => new { schedule.TourId, schedule.StartAtUtc })
            .HasDatabaseName("IX_TourSchedules_Tour");
    }
}