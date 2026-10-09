using FluentAssertions;

using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Entities;

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
        var session = TripSession.Start(
            91,
            80,
            7,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StartedAtUtc,
            StartedAtUtc.AddHours(12),
            [
                Snapshot(1001, 1, isMandatory: true),
                Snapshot(1002, 2, isMandatory: false),
                Snapshot(1003, 3, isMandatory: false),
            ]);
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
    public void ErrorCodes_ReplaceOutOfOrderWithTheRevisionTwoVocabulary()
    {
        NavigationErrorCodes.OutsideTripWindow.Should().Be("navigation.outside_trip_window");
        NavigationErrorCodes.ItemAlreadyReached.Should().Be("navigation.item_already_reached");
        typeof(NavigationErrorCodes).GetField("ItemOutOfOrder").Should().BeNull();
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