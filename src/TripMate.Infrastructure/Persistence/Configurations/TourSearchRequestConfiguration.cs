using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourSearchRequestConfiguration : IEntityTypeConfiguration<TourSearchRequest>
{
    public void Configure(EntityTypeBuilder<TourSearchRequest> builder)
    {
        builder.ToTable("TourSearchRequests", "commerce");

        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id)
            .HasColumnName("search_request_id")
            .ValueGeneratedOnAdd();
        builder.Property(request => request.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(request => request.DestinationText)
            .HasColumnName("destination_text")
            .HasMaxLength(TourSearchRequest.DestinationTextMaxLength);
        builder.Property(request => request.StartDate)
            .HasColumnName("start_date")
            .HasColumnType("date");
        builder.Property(request => request.EndDate)
            .HasColumnName("end_date")
            .HasColumnType("date");
        builder.Property(request => request.MinBudget)
            .HasColumnName("min_budget")
            .HasPrecision(12, 2);
        builder.Property(request => request.MaxBudget)
            .HasColumnName("max_budget")
            .HasPrecision(12, 2);
        builder.Property(request => request.InterestTagsJson)
            .HasColumnName("interest_tags_json")
            .HasMaxLength(TourSearchRequest.InterestTagsJsonMaxLength);
        builder.Property(request => request.RequestedAtUtc)
            .HasColumnName("requested_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(request => request.TravelerUser)
            .WithMany()
            .HasForeignKey(request => request.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(request => request.Recommendations)
            .WithOne(log => log.SearchRequest)
            .HasForeignKey(log => log.SearchRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
