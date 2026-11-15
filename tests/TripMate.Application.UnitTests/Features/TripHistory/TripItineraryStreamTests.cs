using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripItineraryStreamTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset Created = TripHistoryTestData.BaseTime;
    private static readonly DateTimeOffset Start = Created.AddDays(20);
    private static readonly DateTimeOffset Now = Start.AddDays(10);

    private static TripStreamFilter Filter(
        string tab,
        long travelerId = TravelerId,
        VietnamDayRange range = default) =>
        new(travelerId, tab, range);

    private static Task<TripStreamWindow> Read(
        TripHistoryTestData data,
        string tab,
        VietnamDayRange range = default,
        int windowSize = 50,
        long travelerId = TravelerId) =>
        TripItineraryStream.ReadAsync(data.Db, Filter(tab, travelerId, range), Now, windowSize, CancellationToken.None);

    private static async Task<TripHistoryTestData> NewDataAsync()
    {
        var data = new TripHistoryTestData(TripHistoryTestData.CreateDb());
        await data.AddTravelerAsync(TravelerId);
        await data.AddTravelerAsync(OtherTravelerId);
        return data;
    }

    // --- scope (P3) ---

    [Theory]
    [InlineData(Itinerary.ManualSourceType)]
    [InlineData(Itinerary.CspGeneratedSourceType)]
    public async Task Read_ManualAndCspGeneratedAreInScope(string sourceType)
    {
        await using var db = (await NewDataAsync()).Db;
        var data = new TripHistoryTestData(db);
        var itinerary = await data.AddItineraryAsync(
            TravelerId, sourceType: sourceType, schedulingRequestId: sourceType == Itinerary.CspGeneratedSourceType ? 5 : null);

        var window = await Read(data, ListTravelerTripsQuery.TabUpcoming);

        window.Keys.Should().ContainSingle().Which.Id.Should().Be(itinerary.Id);
    }

    [Fact]
    public async Task Read_BookedTourItinerariesAreExcluded()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddItineraryAsync(TravelerId, sourceType: Itinerary.BookedTourSourceType);

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Read_AnotherTravelersItineraryIsNeverReturned()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(OtherTravelerId);
        await data.AddSessionAsync(itinerary, OtherTravelerId, Start, Start.AddHours(5));

        foreach (var tab in ListTravelerTripsQuery.AllowedTabs)
        {
            (await Read(data, tab)).TotalCount.Should().Be(0);
        }
    }

    // --- tabs (P2) ---

    [Fact]
    public async Task Read_DraftIsExcludedFromEveryTab()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddItineraryAsync(TravelerId, Itinerary.DraftStatus);

        foreach (var tab in ListTravelerTripsQuery.AllowedTabs)
        {
            (await Read(data, tab)).TotalCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Read_CompletedStatusWithoutASessionIsExcludedFromEveryTab()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddItineraryAsync(TravelerId, Itinerary.CompletedStatus);

        foreach (var tab in ListTravelerTripsQuery.AllowedTabs)
        {
            (await Read(data, tab)).TotalCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Read_ActiveWithoutACompletedSession_IsUpcoming()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Start); // open, not expired

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(itinerary.Id);
        (await Read(data, ListTravelerTripsQuery.TabCompleted)).TotalCount.Should().Be(0);
        (await Read(data, ListTravelerTripsQuery.TabCancelled)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Read_Cancelled_IsInTheCancelledTabOnly()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, Itinerary.CancelledStatus);

        (await Read(data, ListTravelerTripsQuery.TabCancelled)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(itinerary.Id);
        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).TotalCount.Should().Be(0);
        (await Read(data, ListTravelerTripsQuery.TabCompleted)).TotalCount.Should().Be(0);
    }

    [Theory]
    [InlineData(Itinerary.ActiveStatus)]
    [InlineData(Itinerary.CancelledStatus)]
    [InlineData(Itinerary.CompletedStatus)]
    public async Task Read_AnItineraryWithACompletedSessionIsInExactlyTheCompletedTab(string status)
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, status);
        await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));

        (await Read(data, ListTravelerTripsQuery.TabCompleted)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(itinerary.Id);
        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).TotalCount.Should().Be(0);
        (await Read(data, ListTravelerTripsQuery.TabCancelled)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Read_AnExpiredOpenSessionMakesTheItineraryCompleted()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(2));

        var completed = await Read(data, ListTravelerTripsQuery.TabCompleted);

        completed.Keys.Should().ContainSingle().Which.Should().Be(
            new TripKey(TripRecordKinds.Itinerary, itinerary.Id, session.StartedAtUtc!.Value));
        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).TotalCount.Should().Be(0);
    }

    // --- departure date (P5, T1) ---

    [Fact]
    public async Task Read_CompletedDepartureIsTheRepresentativeSessionStart()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, validFromUtc: Start.AddDays(-3));
        await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        var latest = await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(5));

        var window = await Read(data, ListTravelerTripsQuery.TabCompleted);

        window.TotalCount.Should().Be(1);
        window.Keys.Single().DepartureAtUtc.Should().Be(latest.StartedAtUtc);
    }

    [Fact]
    public async Task Read_UpcomingDepartureIsValidFromThenCreatedAt()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var withValidFrom = await data.AddItineraryAsync(TravelerId, createdAtUtc: Created, validFromUtc: Start);
        var withoutValidFrom = await data.AddItineraryAsync(TravelerId, createdAtUtc: Created.AddDays(1));

        var window = await Read(data, ListTravelerTripsQuery.TabUpcoming);

        window.Keys.Should().Equal(
            new TripKey(TripRecordKinds.Itinerary, withValidFrom.Id, Start),
            new TripKey(TripRecordKinds.Itinerary, withoutValidFrom.Id, Created.AddDays(1)));
    }

    [Fact]
    public async Task Read_DepartureAgreesWithTheClassifier()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, createdAtUtc: Created, validFromUtc: Start);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Start.AddHours(2), Start.AddHours(9));

        var upcomingLikeInputs = TripHistoryClassifier.DepartureForItinerary(null, itinerary.ValidFromUtc, itinerary.CreatedAtUtc);
        var completedInputs = TripHistoryClassifier.DepartureForItinerary(
            session.StartedAtUtc, itinerary.ValidFromUtc, itinerary.CreatedAtUtc);
        var window = await Read(data, ListTravelerTripsQuery.TabCompleted);

        upcomingLikeInputs.Should().Be(Start);
        window.Keys.Single().DepartureAtUtc.Should().Be(completedInputs);
    }

    // --- versions (P4, A1) ---

    [Fact]
    public async Task Read_AnActiveVersionStaysUpcomingWhileANewerDraftIsPending()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var active = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);
        await data.AddItineraryAsync(TravelerId, Itinerary.DraftStatus, schedulingRequestId: 5, version: 2);

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(active.Id);
    }

    [Fact]
    public async Task Read_OnlyTheNewestNonDraftVersionIsShown()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);
        var v2 = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 2);

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(v2.Id);
    }

    [Fact]
    public async Task Read_AVersionCancelledLaterMovesTheTripToCancelled()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);
        var v2 = await data.AddItineraryAsync(TravelerId, Itinerary.CancelledStatus, schedulingRequestId: 5, version: 2);

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).TotalCount.Should().Be(0);
        (await Read(data, ListTravelerTripsQuery.TabCancelled)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(v2.Id);
    }

    [Fact]
    public async Task Read_EqualVersions_TheHigherIdIsNewest()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);
        var second = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task Read_ACompletedOlderVersionAndAnActiveNewerVersionAppearInDifferentTabs()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var v1 = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);
        await data.AddSessionAsync(v1, TravelerId, Start, Start.AddHours(5));
        var v2 = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 2);

        (await Read(data, ListTravelerTripsQuery.TabCompleted)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(v1.Id);
        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).Keys.Should().ContainSingle()
            .Which.Id.Should().Be(v2.Id);
    }

    [Fact]
    public async Task Read_ItinerariesOfDifferentRequestsDoNotHideEachOther()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var a = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 5, version: 1);
        var b = await data.AddItineraryAsync(TravelerId, schedulingRequestId: 6, version: 1);
        var manual = await data.AddItineraryAsync(TravelerId);

        (await Read(data, ListTravelerTripsQuery.TabUpcoming)).Keys.Select(key => key.Id)
            .Should().BeEquivalentTo([a.Id, b.Id, manual.Id]);
    }

    // --- filters, count, window ---

    [Fact]
    public async Task Read_DateFilterAppliesToTheDeparture()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var inside = await data.AddItineraryAsync(TravelerId, validFromUtc: new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero));
        await data.AddItineraryAsync(TravelerId, validFromUtc: new DateTimeOffset(2026, 10, 20, 16, 59, 0, TimeSpan.Zero));
        await data.AddItineraryAsync(TravelerId, validFromUtc: new DateTimeOffset(2026, 10, 21, 17, 0, 0, TimeSpan.Zero));
        VietnamDayRange.TryCreate("2026-10-21", "2026-10-21", out var range).Should().BeTrue();

        var window = await Read(data, ListTravelerTripsQuery.TabUpcoming, range);

        window.TotalCount.Should().Be(1);
        window.Keys.Single().Id.Should().Be(inside.Id);
    }

    [Fact]
    public async Task Read_DateFilterOnCompletedUsesTheSessionStart()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var itinerary = await data.AddItineraryAsync(TravelerId, validFromUtc: Created);
        await data.AddSessionAsync(itinerary, TravelerId, new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        VietnamDayRange.TryCreate("2026-10-21", "2026-10-21", out var onTheDay).Should().BeTrue();
        VietnamDayRange.TryCreate("2026-09-01", "2026-09-30", out var onValidFrom).Should().BeTrue();

        (await Read(data, ListTravelerTripsQuery.TabCompleted, onTheDay)).TotalCount.Should().Be(1);
        (await Read(data, ListTravelerTripsQuery.TabCompleted, onValidFrom)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Read_OrdersByDepartureDescendingThenIdDescending()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        var older = await data.AddItineraryAsync(TravelerId, validFromUtc: Start.AddDays(-5));
        var firstSame = await data.AddItineraryAsync(TravelerId, validFromUtc: Start);
        var secondSame = await data.AddItineraryAsync(TravelerId, validFromUtc: Start);

        var window = await Read(data, ListTravelerTripsQuery.TabUpcoming);

        window.Keys.Select(key => key.Id).Should().Equal(secondSame.Id, firstSame.Id, older.Id);
    }

    [Fact]
    public async Task Read_TotalCountIsIndependentOfTheWindowSize()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;
        for (var day = 0; day < 5; day++)
        {
            await data.AddItineraryAsync(TravelerId, validFromUtc: Start.AddDays(day));
        }

        var window = await Read(data, ListTravelerTripsQuery.TabUpcoming, windowSize: 2);

        window.TotalCount.Should().Be(5);
        window.Keys.Select(key => key.DepartureAtUtc).Should().Equal(Start.AddDays(4), Start.AddDays(3));
    }

    [Fact]
    public async Task Read_NoItineraries_ReturnsAnEmptyWindow()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        var window = await Read(data, ListTravelerTripsQuery.TabUpcoming);

        window.TotalCount.Should().Be(0);
        window.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_NonPositiveWindowOrUnknownTab_Throws()
    {
        var data = await NewDataAsync();
        await using var db = data.Db;

        var window = () => TripItineraryStream.ReadAsync(
            db, Filter(ListTravelerTripsQuery.TabUpcoming), Now, 0, CancellationToken.None);
        var tab = () => TripItineraryStream.ReadAsync(db, Filter("History"), Now, 10, CancellationToken.None);

        await window.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tab.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}