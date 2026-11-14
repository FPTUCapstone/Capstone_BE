using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourRecommendationLogConfiguration : IEntityTypeConfiguration<TourRecommendationLog>
{
    public void Configure(EntityTypeBuilder<TourRecommendationLog> builder)
    {
        builder.ToTable("TourRecommendationLogs", "commerce");

        builder.HasKey(log => log.Id);
        builder.Property(log => log.Id)
            .HasColumnName("recommendation_id")
            .ValueGeneratedOnAdd();
        builder.Property(log => log.SearchRequestId)
            .HasColumnName("search_request_id")
            .IsRequired();
        builder.Property(log => log.TourId)
            .HasColumnName("tour_id")
            .IsRequired();
        builder.Property(log => log.SimilarityScore)
            .HasColumnName("similarity_score")
            .HasPrecision(5, 2)
            .IsRequired();
        builder.Property(log => log.IsRecommended)
            .HasColumnName("is_recommended")
            .IsRequired();
        builder.Property(log => log.RecommendedAtUtc)
            .HasColumnName("recommended_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(log => log.Tour)
            .WithMany()
            .HasForeignKey(log => log.TourId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(log => log.SearchRequestId)
            .HasDatabaseName("IX_TourRecommendationLogs_Search");
    }
}
