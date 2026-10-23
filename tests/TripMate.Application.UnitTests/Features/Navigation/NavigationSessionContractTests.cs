using FluentAssertions;

using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Navigation;

public sealed class NavigationSessionContractTests
{
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Options_DefaultsMatchTheApprovedConfiguration()
    {
        var options = new NavigationSessionOptions();

        NavigationSessionOptions.SectionName.Should().Be("NavigationSession");
        options.EarlyStartWindow.Should().Be(TimeSpan.FromHours(3));
        options.ExpiryGracePeriod.Should().Be(TimeSpan.FromHours(6));
        options.ClientClockSkewTolerance.Should().Be(TimeSpan.FromMinutes(2));
        new NavigationSessionOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OptionsValidator_RejectsNonPositiveWindowsAndNegativeSkew()
    {
        var result = new NavigationSessionOptionsValidator().Validate(
            null,
            new NavigationSessionOptions
            {
                EarlyStartWindow = TimeSpan.Zero,
                ExpiryGracePeriod = TimeSpan.FromMinutes(-1),
                ClientClockSkewTolerance = TimeSpan.FromSeconds(-1),
            });

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(3);
        new NavigationSessionOptionsValidator().Validate(
                null,
                new NavigationSessionOptions { ClientClockSkewTolerance = TimeSpan.Zero })
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Response_ExposesItemStatusScheduleExploringItemAndSuggestedNextItem()
    {
        var session = CreateSession();
        session.SkipItem(1001, StartedAtUtc.AddMinutes(5));
        session.ReachItem(1003, StartedAtUtc.AddMinutes(30));

        var response = NavigationSessionResponse.From(session, itineraryVersion: 3);

        response.Should().BeEquivalentTo(new
        {
            ItineraryId = 91L,
            ItineraryVersion = 3,
            State = TripSession.ExploringState,
            CompletionReason = (string?)null,
            StartedAtUtc,
            ExpiresAtUtc = (DateTimeOffset?)StartedAtUtc.AddHours(12),
            EndedAtUtc = (DateTimeOffset?)null,
            ExploringItemId = (long?)1003,
            NextItemId = (long?)1002,
        });
        response.Items.Select(item => item.Status).Should().Equal(
            TripSessionItem.SkippedStatus,
            TripSessionItem.PendingStatus,
            TripSessionItem.ReachedStatus);
        response.Items.First().Should().BeEquivalentTo(new
        {
            ItemId = 1001L,
            SequenceNo = 1,
            PlannedArrivalUtc = StartedAtUtc.AddHours(1),
            PlannedDepartureUtc = StartedAtUtc.AddHours(2),
            IsMandatory = true,
            ReachedAtUtc = (DateTimeOffset?)null,
            SkippedAtUtc = (DateTimeOffset?)StartedAtUtc.AddMinutes(5),
        });
    }

    [Fact]
    public void Response_SuggestsNoNextItemWhenOnlySkippedItemsRemainButStaysOpen()
    {
        var session = CreateSession();
        session.SkipItem(1001, StartedAtUtc.AddMinutes(5));
        session.ReachItem(1002, StartedAtUtc.AddMinutes(20));
        session.ReachItem(1003, StartedAtUtc.AddMinutes(30));

        var response = NavigationSessionResponse.From(session, itineraryVersion: 3);

        response.State.Should().Be(TripSession.ExploringState);
        response.CompletionReason.Should().BeNull();
        response.NextItemId.Should().BeNull();
        response.Items.First().Status.Should().Be(TripSessionItem.SkippedStatus);
    }

    [Fact]
    public void Response_SuggestsNoNextItemForACompletedSessionWithPendingItems()
    {
        var session = CreateSession();
        session.ReachItem(1001, StartedAtUtc.AddMinutes(5));
        session.Finish(StartedAtUtc.AddMinutes(10));

        var response = NavigationSessionResponse.From(session, itineraryVersion: 3);

        response.State.Should().Be(TripSession.CompletedState);
        response.Items.Should().Contain(item => item.Status == TripSessionItem.PendingStatus);
        response.NextItemId.Should().BeNull();
    }

    [Fact]
    public void Response_TakesEachStopsLegModeFromThePreviousItineraryItemIncludingARest()
    {
        var itinerary = Itinerary.CreateManual(7, "Da Nang", Itinerary.ActiveStatus, StartedAtUtc);
        itinerary.AddItem(Visit(1001, 1, TransportMode.Walking));
        itinerary.AddItem(Visit(1002, 2, TransportMode.Car));
        var rest = ItineraryItem.CreateRest(
            3,
            StartedAtUtc.AddHours(3),
            StartedAtUtc.AddHours(3.5),
            "Rest",
            transportModeToNext: TransportMode.Motorbike);
        rest.Id = 500;
        itinerary.AddItem(rest);
        itinerary.AddItem(Visit(1003, 4, transportModeToNext: null));
        var session = CreateSession(
            Snapshot(1001, 1, isMandatory: true),
            Snapshot(1002, 2, isMandatory: false),
            Snapshot(1003, 4, isMandatory: false));

        var response = NavigationSessionResponse.From(session, itinerary);

        response.Items.Select(item => item.TransportModeFromPrevious).Should().Equal(
            null,
            TransportMode.Walking,
            TransportMode.Motorbike);
    }

    [Fact]
    public void Response_LeavesLegModesUnknownWithoutItineraryItems()
    {
        var itinerary = Itinerary.CreateManual(7, "Da Nang", Itinerary.ActiveStatus, StartedAtUtc);

        var fromItinerary = NavigationSessionResponse.From(CreateSession(), itinerary);
        var fromVersion = NavigationSessionResponse.From(CreateSession(), itineraryVersion: 3);

        fromItinerary.Items.Should().OnlyContain(item => item.TransportModeFromPrevious == null);
        fromVersion.Items.Should().OnlyContain(item => item.TransportModeFromPrevious == null);
    }

    [Fact]
    public void ErrorCodes_ReplaceOutOfOrderWithTheRevisionTwoVocabulary()
    {
        NavigationErrorCodes.OutsideTripWindow.Should().Be("navigation.outside_trip_window");
        NavigationErrorCodes.ItemAlreadyReached.Should().Be("navigation.item_already_reached");
        NavigationErrorCodes.ItineraryScheduleIncomplete.Should().Be("navigation.itinerary_schedule_incomplete");
        typeof(NavigationErrorCodes).GetField("ItemOutOfOrder").Should().BeNull();
    }

    private static TripSession CreateSession() =>
        CreateSession(
            Snapshot(1001, 1, isMandatory: true),
            Snapshot(1002, 2, isMandatory: false),
            Snapshot(1003, 3, isMandatory: false));

    private static TripSession CreateSession(params TripSessionItem[] items) =>
        TripSession.Start(
            91,
            80,
            7,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StartedAtUtc,
            StartedAtUtc.AddHours(12),
            items);

    private static ItineraryItem Visit(long itemId, int sequenceNo, TransportMode? transportModeToNext)
    {
        var item = ItineraryItem.CreateVisit(
            sequenceNo,
            itemId,
            StartedAtUtc.AddHours(sequenceNo),
            StartedAtUtc.AddHours(sequenceNo + 0.5),
            isMandatory: false,
            estimatedCost: null,
            recommendationReason: null,
            transportModeToNext: transportModeToNext);
        item.Id = itemId;
        return item;
    }

    private static TripSessionItem Snapshot(long itemId, int sequenceNo, bool isMandatory) =>
        TripSessionItem.Snapshot(
            itemId,
            sequenceNo,
            itemId,
            $"POI {sequenceNo}",
            16m,
            108m,
            StartedAtUtc.AddHours(sequenceNo),
            StartedAtUtc.AddHours(sequenceNo + 1),
            isMandatory);
}