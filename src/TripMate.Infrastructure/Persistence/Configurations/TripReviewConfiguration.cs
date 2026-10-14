using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripReviewConfiguration : IEntityTypeConfiguration<TripReview>
{
    public void Configure(EntityTypeBuilder<TripReview> builder)
    {
        builder.ToTable("TripReviews", "social");
        builder.HasKey(review => review.Id);
        builder.Property(review => review.Id).HasColumnName("trip_review_id").ValueGeneratedOnAdd();
        // Bookings has no mapped entity at this base. SQL retains its real restrictive FK.
        builder.Property(review => review.BookingId).HasColumnName("booking_id");
        builder.Property(review => review.ServiceBookingId).HasColumnName("service_booking_id");
        builder.Property(review => review.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(review => review.TourId).HasColumnName("tour_id");
        builder.Property(review => review.ItineraryId).HasColumnName("itinerary_id");
        builder.Property(review => review.PoiId).HasColumnName("poi_id");
        builder.Property(review => review.OverallRating).HasColumnName("overall_rating").IsRequired();
        builder.Property(review => review.Title).HasColumnName("title")
            .HasMaxLength(TripReview.TitleMaxLength).UseCollation("Vietnamese_100_CI_AS").IsRequired();
        builder.Property(review => review.Content).HasColumnName("content")
            .HasMaxLength(TripReview.ContentMaxLength).UseCollation("Vietnamese_100_CI_AS").IsRequired();
        builder.Property(review => review.RoutePacing).HasColumnName("route_pacing")
            .HasConversion(value => EncodePacing(value), value => DecodePacing(value))
            .HasMaxLength(9).IsUnicode(false);
        builder.Property(review => review.CspRating).HasColumnName("csp_rating");
        builder.Property(review => review.PublishDisplayName).HasColumnName("publish_display_name").IsRequired();
        builder.Property(review => review.PublicDisplayName).HasColumnName("public_display_name")
            .UseCollation("Vietnamese_100_CI_AS").IsRequired();
        builder.Property(review => review.PublicationStatus).HasColumnName("publication_status")
            .HasMaxLength(9).IsUnicode(false).IsRequired();
        builder.Property(review => review.PolicyVersion).HasColumnName("policy_version")
            .UseCollation("Vietnamese_100_CI_AS").IsRequired(false);
        builder.Property(review => review.CreatedAtUtc).HasColumnName("created_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        builder.Property(review => review.EditDeadlineUtc).HasColumnName("edit_deadline").HasColumnType("datetime2(7)").AsUtcDateTime2();
        builder.Property(review => review.UpdatedAtUtc).HasColumnName("updated_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        builder.Property(review => review.Version).HasColumnName("version").IsRowVersion();
        builder.HasOne<User>().WithMany().HasForeignKey(review => review.TravelerUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Tour>().WithMany().HasForeignKey(review => review.TourId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Itinerary>().WithMany().HasForeignKey(review => review.ItineraryId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PointOfInterest>().WithMany().HasForeignKey(review => review.PoiId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(review => review.BookingId).IsUnique().HasFilter("[booking_id] IS NOT NULL")
            .HasDatabaseName("UX_TripReviews_CommerceBooking");
        builder.HasIndex(review => review.ServiceBookingId).IsUnique().HasFilter("[service_booking_id] IS NOT NULL")
            .HasDatabaseName("UX_TripReviews_ServiceBooking");
    }

    private static string? EncodePacing(RoutePacingFeedback? value) => value switch
    {
        null => null,
        RoutePacingFeedback.TooTight => "tooTight",
        RoutePacingFeedback.WellPaced => "wellPaced",
        RoutePacingFeedback.TooLoose => "tooLoose",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static RoutePacingFeedback? DecodePacing(string? value) => value switch
    {
        null => null,
        "tooTight" => RoutePacingFeedback.TooTight,
        "wellPaced" => RoutePacingFeedback.WellPaced,
        "tooLoose" => RoutePacingFeedback.TooLoose,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}