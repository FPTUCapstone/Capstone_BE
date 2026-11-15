using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class ListTravelerTripsQueryHandlerTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(90);

    private static ListTravelerTripsQuery Query(
        string tab = ListTravelerTripsQuery.TabCompleted,
        string? tripType = null,
        string? fromDate = null,
        string? toDate = null,
        int page = 1,
        int pageSize = 20) =>
        new(TravelerId, tab, tripType, fromDate, toDate, page, pageSize);

    private static Task<TripMate.Application.Common.Models.Result<TripHistoryPage>> Handle(
        TripHistoryTestData data, ListTravelerTripsQuery query) =>
        new ListTravelerTripsQueryHandler(data.Db, new FakeDateTimeProvider { UtcNow = Now })
            .Handle(query, CancellationToken.None);

    private static async Task<TripHistoryTestData> NewDataAsync()
    {
        var data = new TripHistoryTestData(TripHistoryTestData.CreateDb());
        await data.AddTravelerAsync(TravelerId);
        await data.AddTravelerAsync(OtherTravelerId);
        return data;
    }

    [Fact]
    public async Task Handle_ReturnsHydratedItemsInOrderWithPagingAndTotal()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var older = await data.AddScheduleAsync(Start);
        var olderBooking = await data.AddBookingAsync(TravelerId, older, BookingStatus.Completed);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(5), Start.AddDays(5).AddHours(6), reachedItems: 2);

        var result = await Handle(data, Query());

        result.IsSuccess.Should().BeTrue();
        var page = result.Value;
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(2);
        page.Items.Select(item => (item.Kind, item.Id)).Should().Equal(
            (TripRecordKinds.Itinerary, itinerary.Id),
            (TripRecordKinds.CommerceBooking, olderBooking.Id));
        page.Items.First().VisitedCount.Should().Be(2);
        page.Items.Last().Actions.CanReview.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_PagesAreStableAndTheTotalStaysTheSame()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        for (var day = 0; day < 5; day++)
        {
            var schedule = await data.AddScheduleAsync(Start.AddDays(day));
            await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        }

        var first = (await Handle(data, Query(page: 1, pageSize: 2))).Value;
        var second = (await Handle(data, Query(page: 2, pageSize: 2))).Value;
        var third = (await Handle(data, Query(page: 3, pageSize: 2))).Value;

        first.TotalCount.Should().Be(5);
        second.TotalCount.Should().Be(5);
        third.TotalCount.Should().Be(5);
        first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.DepartureAtUtc)
            .Should().Equal(Enumerable.Range(0, 5).Reverse().Select(day => Start.AddDays(day)));
        third.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_EmptyTab_ReturnsNoItemsAndZero()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        var result = await Handle(data, Query(ListTravelerTripsQuery.TabUpcoming));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.Summary.Should().BeNull();
    }

    // --- summary (P9) ---

    [Theory]
    [InlineData(ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(ListTravelerTripsQuery.TabCancelled)]
    public async Task Summary_IsOmittedOutsideTheCompletedTab(string tab)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        (await Handle(data, Query(tab))).Value.Summary.Should().BeNull();
    }

    [Fact]
    public async Task Summary_EmptyCompletedTab_IsZeroNotNull()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        (await Handle(data, Query())).Value.Summary.Should().Be(new TripHistorySummaryResponse(0, 0));
    }

    [Fact]
    public async Task Summary_TotalCompletedTripsIsTheFilteredTotalNotThePageSize()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        for (var day = 0; day < 5; day++)
        {
            var schedule = await data.AddScheduleAsync(Start.AddDays(day));
            await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        }

        var page = (await Handle(data, Query(pageSize: 2))).Value;

        page.Items.Should().HaveCount(2);
        page.Summary.Should().Be(new TripHistorySummaryResponse(5, 0));
        page.Summary!.TotalCompletedTrips.Should().Be(page.TotalCount);
    }

    [Fact]
    public async Task Summary_VisitedPoisCountsReachedItemsOfTheRepresentativeSessionsOnly()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var first = await data.AddItineraryAsync(TravelerId, visitCount: 4);
        // An older completed session of the same itinerary must not be counted.
        await data.AddSessionAsync(first, TravelerId, Start, Start.AddHours(3), reachedItems: 4);
        await data.AddSessionAsync(first, TravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(3), reachedItems: 3);
        var second = await data.AddItineraryAsync(TravelerId, visitCount: 2);
        await data.AddSessionAsync(second, TravelerId, Start.AddDays(2), Start.AddDays(2).AddHours(3), reachedItems: 1);
        var third = await data.AddItineraryAsync(OtherTravelerId, visitCount: 2);
        await data.AddSessionAsync(third, OtherTravelerId, Start.AddDays(2), Start.AddDays(2).AddHours(3), reachedItems: 2);

        var summary = (await Handle(data, Query())).Value.Summary;

        summary.Should().Be(new TripHistorySummaryResponse(2, 4));
    }

    [Fact]
    public async Task Summary_VisitedPoisFollowTheTripTypeAndDateFilters()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var schedule = await data.AddScheduleAsync(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero));
        await data.AddBookingAsync(TravelerId, schedule, BookingStatus.Completed);
        var onTheDay = await data.AddItineraryAsync(TravelerId, visitCount: 3);
        await data.AddSessionAsync(onTheDay, TravelerId, new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero), reachedItems: 3);
        var otherDay = await data.AddItineraryAsync(TravelerId, visitCount: 2);
        await data.AddSessionAsync(otherDay, TravelerId, new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero), reachedItems: 2);

        var all = (await Handle(data, Query())).Value.Summary;
        var dated = (await Handle(data, Query(fromDate: "2026-10-21", toDate: "2026-10-21"))).Value.Summary;
        var bookingsOnly = (await Handle(data, Query(tripType: ListTravelerTripsQuery.TripTypeTourBooking))).Value.Summary;
        var itinerariesOnly = (await Handle(data, Query(tripType: ListTravelerTripsQuery.TripTypeSelfPlannedItinerary))).Value.Summary;

        all.Should().Be(new TripHistorySummaryResponse(3, 5));
        dated.Should().Be(new TripHistorySummaryResponse(2, 3));
        bookingsOnly.Should().Be(new TripHistorySummaryResponse(1, 0));
        itinerariesOnly.Should().Be(new TripHistorySummaryResponse(2, 5));
    }

    [Fact]
    public async Task Summary_CountsVisitsOfASessionThatCompletedByExpiry()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, visitCount: 3);
        await data.AddSessionAsync(itinerary, TravelerId, Start, reachedItems: 2, lifetime: TimeSpan.FromDays(2));

        (await Handle(data, Query())).Value.Summary.Should().Be(new TripHistorySummaryResponse(1, 2));
    }
}