using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripHistoryClassifierTests
{
    [Theory]
    [InlineData(BookingStatus.PendingPayment, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Confirmed, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(BookingStatus.Completed, ListTravelerTripsQuery.TabCompleted)]
    [InlineData(BookingStatus.Cancelled, ListTravelerTripsQuery.TabCancelled)]
    [InlineData(BookingStatus.Expired, ListTravelerTripsQuery.TabCancelled)]
    public void TabForBooking_MapsStoredStatusOnly(BookingStatus status, string expectedTab)
    {
        TripHistoryClassifier.TabForBooking(status).Should().Be(expectedTab);
    }

    [Fact]
    public void TabForBooking_CoversEveryBookingStatus()
    {
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            TripHistoryClassifier.TabForBooking(status).Should().BeOneOf(ListTravelerTripsQuery.AllowedTabs);
        }
    }

    [Fact]
    public void TabForBooking_UnknownStatus_Throws()
    {
        var act = () => TripHistoryClassifier.TabForBooking((BookingStatus)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(Itinerary.ActiveStatus)]
    [InlineData(Itinerary.CancelledStatus)]
    [InlineData(Itinerary.CompletedStatus)]
    public void TabForItinerary_CompletedSessionAlwaysWins(string itineraryStatus)
    {
        TripHistoryClassifier.TabForItinerary(itineraryStatus, hasCompletedSession: true)
            .Should().Be(ListTravelerTripsQuery.TabCompleted);
    }

    [Theory]
    [InlineData(Itinerary.ActiveStatus, ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(Itinerary.CancelledStatus, ListTravelerTripsQuery.TabCancelled)]
    public void TabForItinerary_WithoutCompletedSession_FollowsItineraryStatus(string itineraryStatus, string expectedTab)
    {
        TripHistoryClassifier.TabForItinerary(itineraryStatus, hasCompletedSession: false)
            .Should().Be(expectedTab);
    }

    [Fact]
    public void TabForItinerary_DraftWithoutSession_IsExcluded()
    {
        TripHistoryClassifier.TabForItinerary(Itinerary.DraftStatus, hasCompletedSession: false)
            .Should().BeNull();
    }

    [Fact]
    public void TabForItinerary_CompletedStatusWithoutSession_IsExcluded()
    {
        // Itinerary.Status = Completed is never assigned today; the Completed tab is defined by the session.
        TripHistoryClassifier.TabForItinerary(Itinerary.CompletedStatus, hasCompletedSession: false)
            .Should().BeNull();
    }

    [Fact]
    public void TabForItinerary_UnknownStatus_IsExcluded()
    {
        TripHistoryClassifier.TabForItinerary("Archived", hasCompletedSession: false).Should().BeNull();
    }

    [Fact]
    public void DepartureForItinerary_PrefersTheCompletedSessionStart()
    {
        var sessionStart = new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero);
        var validFrom = new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
        var created = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        TripHistoryClassifier.DepartureForItinerary(sessionStart, validFrom, created).Should().Be(sessionStart);
    }

    [Fact]
    public void DepartureForItinerary_WithoutSession_UsesValidFrom()
    {
        var validFrom = new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
        var created = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        TripHistoryClassifier.DepartureForItinerary(null, validFrom, created).Should().Be(validFrom);
    }

    [Fact]
    public void DepartureForItinerary_WithoutSessionOrValidFrom_UsesCreatedAt()
    {
        var created = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        TripHistoryClassifier.DepartureForItinerary(null, null, created).Should().Be(created);
    }

    [Fact]
    public void RecordKinds_MatchTheTm79WireValuesAndAreDistinct()
    {
        TripRecordKinds.CommerceBooking.Should().Be(ReviewableRecordRef.CommerceBookingKind).And.Be("commerceBooking");
        TripRecordKinds.Itinerary.Should().Be("itinerary");
        TripRecordKinds.All.Should().BeEquivalentTo([TripRecordKinds.CommerceBooking, TripRecordKinds.Itinerary]);
    }

    [Fact]
    public void ErrorCodes_AreNamespacedAndUnique()
    {
        string[] codes =
        [
            TripHistoryErrorCodes.InvalidTab,
            TripHistoryErrorCodes.InvalidTripType,
            TripHistoryErrorCodes.InvalidDateRange,
            TripHistoryErrorCodes.InvalidPaging,
            TripHistoryErrorCodes.InvalidRecord,
            TripHistoryErrorCodes.Unauthorized,
            TripHistoryErrorCodes.Forbidden,
            TripHistoryErrorCodes.RecordNotFound,
        ];

        codes.Should().OnlyHaveUniqueItems().And.OnlyContain(code => code.StartsWith("trip_history."));
        TripHistoryErrorCodes.InvalidPaging.Should().Be("trip_history.invalid_paging");
        TripHistoryErrorCodes.RecordNotFound.Should().Be("trip_history.record_not_found");
    }

    [Fact]
    public void ListQueryConstants_MatchTheContract()
    {
        ListTravelerTripsQuery.AllowedTabs.Should().Equal("Upcoming", "Completed", "Cancelled");
        ListTravelerTripsQuery.AllowedTripTypes.Should().Equal("All", "TourBooking", "SelfPlannedItinerary");
        ListTravelerTripsQuery.DefaultPageSize.Should().Be(20);
        ListTravelerTripsQuery.MaxPageSize.Should().Be(50);
        ListTravelerTripsQuery.MaxWindowKeys.Should().Be(1000);
    }
}