using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps commerce.Tours from database/tripmate_schema_v7.sql.
/// </summary>
public class TourConfiguration : IEntityTypeConfiguration<Tour>
{
    public void Configure(EntityTypeBuilder<Tour> builder)
    {
        builder.ToTable("Tours", "commerce");

        builder.HasKey(tour => tour.Id);
        builder.Property(tour => tour.Id)
            .HasColumnName("tour_id")
            .ValueGeneratedOnAdd();

        builder.Property(tour => tour.OperatorUserId)
            .HasColumnName("operator_user_id")
            .IsRequired();
        builder.Property(tour => tour.Title)
            .HasColumnName("title")
            .HasMaxLength(Tour.TitleMaxLength)
            .IsRequired();
        builder.Property(tour => tour.Description)
            .HasColumnName("description");
        builder.Property(tour => tour.BasePrice)
            .HasColumnName("base_price")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(tour => tour.DurationDays)
            .HasColumnName("duration_days")
            .HasDefaultValue(1)
            .IsRequired();
        builder.Property(tour => tour.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(12)
            .IsUnicode(false)
            .HasDefaultValueSql("'Draft'")
            .IsRequired();
        builder.Property(tour => tour.RejectionReason)
            .HasColumnName("rejection_reason")
            .HasMaxLength(Tour.RejectionReasonMaxLength);
        builder.Property(tour => tour.ReviewedBy)
            .HasColumnName("reviewed_by");
        builder.Property(tour => tour.ReviewedAtUtc)
            .HasColumnName("reviewed_at")
            .AsUtcDateTime2();
        builder.Property(tour => tour.PublishedAtUtc)
            .HasColumnName("published_at")
            .AsUtcDateTime2();
        builder.Property(tour => tour.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(tour => tour.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(tour => tour.OperatorProfile)
            .WithMany()
            .HasForeignKey(tour => tour.OperatorUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(tour => tour.Reviewer)
            .WithMany()
            .HasForeignKey(tour => tour.ReviewedBy)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(tour => tour.Schedules)
            .WithOne(schedule => schedule.Tour)
            .HasForeignKey(schedule => schedule.TourId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(tour => tour.Destinations)
            .WithOne(link => link.Tour)
            .HasForeignKey(link => link.TourId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(tour => tour.Schedules)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(tour => tour.Destinations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(tour => tour.OperatorUserId)
            .HasDatabaseName("IX_Tours_Operator");
        builder.HasIndex(tour => tour.Status)
            .HasDatabaseName("IX_Tours_Status");
    }
}