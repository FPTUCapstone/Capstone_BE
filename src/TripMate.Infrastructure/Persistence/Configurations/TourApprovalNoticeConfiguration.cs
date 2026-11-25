using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourApprovalNoticeConfiguration : IEntityTypeConfiguration<TourApprovalNotice>
{
    public void Configure(EntityTypeBuilder<TourApprovalNotice> builder)
    {
        builder.ToTable("TourApprovalNoticeOutbox", "commerce");
        builder.HasKey(notice => notice.TourId);
        builder.Property(notice => notice.TourId).HasColumnName("tour_id").ValueGeneratedNever();
        builder.Property(notice => notice.OperatorUserId).HasColumnName("operator_user_id");
        builder.Property(notice => notice.TourTitle).HasColumnName("tour_title").HasMaxLength(Tour.TitleMaxLength);
        builder.Property(notice => notice.ApprovedAtUtc).HasColumnName("approved_at_utc").AsUtcDateTime2();
        builder.Property(notice => notice.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc").AsUtcDateTime2();
        builder.HasIndex(notice => new { notice.NextAttemptAtUtc, notice.TourId })
            .HasDatabaseName("IX_TourApprovalNoticeOutbox_Due");
    }
}