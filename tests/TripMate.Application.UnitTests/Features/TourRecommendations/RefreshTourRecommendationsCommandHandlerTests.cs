using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class RefreshTourRecommendationsCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task Handle_MissingOrMalformedInterestTags_ReturnsTypedFailureWithoutSnapshot(string? json)
    {
        await using var db = TestDbContext.Create();
        db.TravelerProfiles.Add(TravelerProfile.Create(7, json, Now));
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, []);

        var result = await handler.Handle(new RefreshTourRecommendationsCommand(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TourRecommendationErrorCodes.InterestTagsRequired);
        (await db.TourSearchRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_PersistsOneSnapshotAndEveryCandidateLogAtomically()
    {
        await using var db = TestDbContext.Create();
        db.TravelerProfiles.Add(TravelerProfile.Create(7, "[\"beach\"]", Now));
        await db.SaveChangesAsync();
        await PublicTourTestData.SeedOperatorAsync(db, 8);
        var recommendedTour = await PublicTourTestData.AddTourAsync(
            db,
            8,
            TourStatus.Published,
            "Tour 11",
            [(Now.AddDays(10), 10, 1_000_000m)]);
        var rejectedTour = await PublicTourTestData.AddTourAsync(
            db,
            8,
            TourStatus.Published,
            "Tour 12",
            [(Now.AddDays(10), 10, 1_000_000m)]);
        int savesBefore = db.SaveChangesAsyncCallCount;
        var candidates = new[]
        {
            Candidate(recommendedTour.Id, ["beach"]),
            Candidate(rejectedTour.Id, ["culture"]),
        };
        var handler = CreateHandler(db, candidates);

        var result = await handler.Handle(
            new RefreshTourRecommendationsCommand(Destination: " Da Nang "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items.Single().MatchingReasons.Should().NotBeNull().And.BeEmpty();
        db.TransactionExecutionCount.Should().Be(1);
        (db.SaveChangesAsyncCallCount - savesBefore).Should().Be(2);
        var snapshot = await db.TourSearchRequests.Include(request => request.Recommendations).SingleAsync();
        snapshot.DestinationText.Should().Be("Da Nang");
        snapshot.Recommendations.Should().HaveCount(2);
        snapshot.Recommendations.Single(log => log.TourId == recommendedTour.Id).IsRecommended.Should().BeTrue();
        snapshot.Recommendations.Single(log => log.TourId == rejectedTour.Id).IsRecommended.Should().BeFalse();
    }

    private static RefreshTourRecommendationsCommandHandler CreateHandler(
        TestDbContext db,
        IReadOnlyList<TourRecommendationCandidate> candidates) =>
        new(db, new CurrentUser(7), new Clock(Now), new CandidateReader(candidates));

    private static TourRecommendationCandidate Candidate(long id, IReadOnlyCollection<string> interests) =>
        new(
            id,
            $"Tour {id}",
            1_000_000m,
            2,
            "Operator",
            ["Da Nang"],
            interests,
            ["Da Nang"],
            new TourRecommendationCandidateSchedule(
                id * 10,
                Now.AddDays(10),
                Now.AddDays(11),
                10,
                0,
                null));

    private sealed record CurrentUser(long Id) : ICurrentUserService
    {
        public long? UserId => Id;
        public string? Role => "Traveler";
    }

    private sealed record Clock(DateTimeOffset Now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class CandidateReader(IReadOnlyList<TourRecommendationCandidate> candidates)
        : ITourRecommendationCandidateReader
    {
        public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
            DateTimeOffset nowUtc,
            DateOnly? startDate,
            DateOnly? endDate,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return candidates;
        }
    }
}
