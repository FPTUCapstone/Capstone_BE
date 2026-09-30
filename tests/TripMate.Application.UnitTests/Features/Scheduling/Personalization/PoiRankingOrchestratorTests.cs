using System.Collections.Frozen;
using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PoiRankingOrchestratorTests
{
    [Fact]
    public async Task BuildRankingAsync_WithNoCandidates_ReturnsEmptyProviderSkippedSnapshot()
    {
        var dbContext = new Mock<IApplicationDbContext>(MockBehavior.Strict);
        var provider = new RecordingProvider();
        var orchestrator = CreateOrchestrator(dbContext.Object, provider, providerEnabled: true);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, ["culture"], []),
            CancellationToken.None);

        snapshot.ProviderPoolPoiIds.Should().BeEmpty();
        snapshot.Entries.Should().BeEmpty();
        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.ProviderSkipped);
        provider.CallCount.Should().Be(0);
        dbContext.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BuildRankingAsync_WithCandidates_ExecutesOneAggregationBatchAndLocalPipeline()
    {
        await using var dbContext = CreateDbContext();
        var countingContext = new Mock<IApplicationDbContext>(MockBehavior.Strict);
        countingContext.SetupGet(context => context.RecommendationBehaviorEvents)
            .Returns(dbContext.RecommendationBehaviorEvents);
        countingContext.SetupGet(context => context.PointsOfInterest)
            .Returns(dbContext.PointsOfInterest);
        var orchestrator = CreateOrchestrator(
            countingContext.Object,
            new RecordingProvider(),
            providerEnabled: false);
        var candidate = Candidate(
            11L,
            categoryName: "Culture",
            tags: ["museum", "heritage", "history"],
            scenicScore: 10m,
            photoRating: 10m);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(
                17L,
                ["culture", "museum", "heritage", "history"],
                [candidate]),
            CancellationToken.None);

        snapshot.Entries[11L].TripMateBaseScore.Should().Be(0.875m);
        snapshot.Entries[11L].EffectiveDesirabilityScore.Should().Be(0.875m);
        countingContext.VerifyGet(
            context => context.RecommendationBehaviorEvents,
            Times.Exactly(2));
        countingContext.VerifyGet(context => context.PointsOfInterest, Times.Once);
    }

    [Theory]
    [MemberData(nameof(PoolOrderingCases))]
    public async Task BuildRankingAsync_SelectsCandidateByDeterministicTotalOrder(
        string scenario)
    {
        await using var dbContext = CreateDbContext();
        var (preferenceTokens, first, second, expectedPoiId, useDefaultWeights) =
            PoolOrderingCase(scenario);
        var options = useDefaultWeights
            ? Options(maxProviderCandidates: 1)
            : TieOnlyOptions(maxProviderCandidates: 1);
        var orchestrator = CreateOrchestrator(
            dbContext,
            new RecordingProvider(),
            providerEnabled: false,
            options);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, preferenceTokens, [second, first]),
            CancellationToken.None);

        snapshot.ProviderPoolPoiIds.Should().Equal(expectedPoiId);
        snapshot.Entries.Keys.Should().Equal(expectedPoiId);
    }

    [Fact]
    public async Task BuildRankingAsync_WithShuffledInput_SelectsSamePool()
    {
        await using var firstContext = CreateDbContext();
        await using var secondContext = CreateDbContext();
        var candidates = Enumerable.Range(1, 6)
            .Select(id => Candidate(id, scenicScore: id))
            .ToArray();
        var options = TieOnlyOptions(maxProviderCandidates: 3);

        PoiRankingSnapshot first = await CreateOrchestrator(
                firstContext,
                new RecordingProvider(),
                providerEnabled: false,
                options)
            .BuildRankingAsync(new PoiRankingInput(17L, [], candidates), CancellationToken.None);
        PoiRankingSnapshot second = await CreateOrchestrator(
                secondContext,
                new RecordingProvider(),
                providerEnabled: false,
                options)
            .BuildRankingAsync(
                new PoiRankingInput(17L, [], candidates.Reverse().ToArray()),
                CancellationToken.None);

        first.ProviderPoolPoiIds.Should().BeEquivalentTo([4L, 5L, 6L]);
        second.ProviderPoolPoiIds.Should().BeEquivalentTo(first.ProviderPoolPoiIds);
    }

    [Fact]
    public async Task BuildRankingAsync_OverCap_SendsAndReturnsOnlyTopSixty()
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider();
        var candidates = Enumerable.Range(1, 61)
            .Select(id => Candidate(id, scenicScore: id <= 10 ? id : 10m))
            .ToArray();
        var orchestrator = CreateOrchestrator(
            dbContext,
            provider,
            providerEnabled: true,
            TieOnlyOptions(maxProviderCandidates: 60));

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], candidates),
            CancellationToken.None);

        snapshot.ProviderPoolPoiIds.Should().HaveCount(60);
        snapshot.Entries.Should().HaveCount(60);
        provider.LastRequest!.Candidates.Should().HaveCount(60);
        snapshot.ProviderPoolPoiIds.Should().NotContain(1L);
        provider.LastRequest.Candidates.Should().NotContain(candidate => candidate.PoiId == 1L);
        snapshot.Entries.Should().NotContainKey(1L);
        provider.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildRankingAsync_WhenDisabled_StillCapsPoolAndUsesBase()
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider();
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var candidates = Enumerable.Range(1, 4)
            .Select(id => Candidate(id, scenicScore: id))
            .ToArray();
        var orchestrator = CreateOrchestrator(
            dbContext,
            provider,
            providerEnabled: false,
            TieOnlyOptions(maxProviderCandidates: 3),
            logger);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], candidates),
            CancellationToken.None);

        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.ProviderSkipped);
        snapshot.Entries.Should().HaveCount(3);
        snapshot.Entries.Values.Should().OnlyContain(entry =>
            entry.EffectiveDesirabilityScore == entry.TripMateBaseScore);
        snapshot.ProviderPoolPoiIds.Should().NotContain(1L);
        provider.CallCount.Should().Be(0);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Information);
        log.Properties.Should().Contain(new Dictionary<string, object?>
        {
            ["Enabled"] = false,
            ["CandidateCount"] = 4,
            ["PoolCount"] = 3,
            ["Outcome"] = "provider-skipped",
            ["FallbackUsed"] = false,
            ["TimedOut"] = false,
        });
        log.Message.Should().NotContain("FAKE_SECRET_SHOULD_NOT_APPEAR");
    }

    [Fact]
    public async Task BuildRankingAsync_BuildsPoolOnlySemanticProviderPayload()
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider();
        var orchestrator = CreateOrchestrator(
            dbContext,
            provider,
            providerEnabled: true,
            TieOnlyOptions(maxProviderCandidates: 1));
        var selected = Candidate(
            2L,
            categoryId: 99,
            name: "Museum",
            categoryName: "Culture",
            tags: ["heritage"],
            scenicScore: 9m,
            photoRating: 8m,
            distance: 1.2m,
            cost: 25m);

        await orchestrator.BuildRankingAsync(
            new PoiRankingInput(123L, ["culture"], [Candidate(1L), selected]),
            CancellationToken.None);

        PoiRankingRequest request = provider.LastRequest!;
        request.Context.PreferenceTokens.Should().Equal("culture");
        request.Candidates.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PoiRankingCandidate(2L, "Museum", "Culture", ["heritage"]));
        PublicPropertyNames<PoiRankingRequest>().Should().BeEquivalentTo("Candidates", "Context");
        PublicPropertyNames<PoiRankingContext>().Should().BeEquivalentTo("PreferenceTokens");
        PublicPropertyNames<PoiRankingCandidate>().Should().BeEquivalentTo(
            "PoiId", "Name", "CategoryName", "TagNames");
        provider.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0.60, 0.40, 0.80, 0.62)]
    [InlineData(0.25, 0.75, 0.80, 0.725)]
    public async Task BuildRankingAsync_WithValidResponse_UsesConfiguredBlend(
        double baseWeight,
        double aiWeight,
        double aiScore,
        double expected)
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = (request, _) => Task.FromResult(Result.Success(
                new PoiRankingResult(
                    request.Candidates.Reverse()
                        .Select(candidate => new PoiRankingItem(
                            candidate.PoiId,
                            (decimal)aiScore,
                            " reason "))
                        .ToArray()))),
        };
        var options = TieOnlyOptions(
            maxProviderCandidates: 2,
            baseWeight: (decimal)baseWeight,
            aiWeight: (decimal)aiWeight);
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(dbContext, provider, true, options, logger);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L), Candidate(2L)]),
            CancellationToken.None);

        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.Success);
        snapshot.ProviderPoolPoiIds.Should().BeEquivalentTo([1L, 2L]);
        provider.LastRequest!.Candidates.Should().HaveCount(2);
        snapshot.Entries.Values.Should().OnlyContain(entry =>
            entry.EffectiveDesirabilityScore == (decimal)expected);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Information);
        log.Properties["Provider"].Should().Be(nameof(RecordingProvider));
        log.Properties["Enabled"].Should().Be(true);
        log.Properties["CandidateCount"].Should().Be(2);
        log.Properties["PoolCount"].Should().Be(2);
        log.Properties["LatencyMs"].Should().BeOfType<long>().Which.Should().BeGreaterThanOrEqualTo(0);
        log.Properties["Outcome"].Should().Be("success");
        log.Properties["FallbackUsed"].Should().Be(false);
        log.Properties["TimedOut"].Should().Be(false);
    }

    [Fact]
    public async Task BuildRankingAsync_WithReorderedResponse_MapsAiScoresByPoiId()
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = (_, _) => Task.FromResult(Result.Success(
                new PoiRankingResult(
                [
                    new PoiRankingItem(2L, 0.9m, "longer explanation"),
                    new PoiRankingItem(1L, 0.1m, null),
                ]))),
        };
        var orchestrator = CreateOrchestrator(
            dbContext,
            provider,
            true,
            TieOnlyOptions(maxProviderCandidates: 2));

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L), Candidate(2L)]),
            CancellationToken.None);

        snapshot.Entries[1L].EffectiveDesirabilityScore.Should().Be(0.34m);
        snapshot.Entries[2L].EffectiveDesirabilityScore.Should().Be(0.66m);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  useful reason  ", "useful reason")]
    public void TryValidateProviderResult_NormalizesOptionalReason(
        string? reason,
        string? expected)
    {
        FrozenSet<long> poolIds = new[] { 7L }.ToFrozenSet();

        bool valid = PoiRankingOrchestrator.TryValidateProviderResult(
            new PoiRankingResult([new PoiRankingItem(7L, 0.5m, reason)]),
            poolIds,
            out FrozenDictionary<long, ValidatedPoiRankingItem> items);

        valid.Should().BeTrue();
        items[7L].Reason.Should().Be(expected);
    }

    [Fact]
    public void TryValidateProviderResult_WithLongReason_TruncatesWithoutChangingScore()
    {
        FrozenSet<long> poolIds = new[] { 7L }.ToFrozenSet();

        bool valid = PoiRankingOrchestrator.TryValidateProviderResult(
            new PoiRankingResult([new PoiRankingItem(7L, 0.75m, new string('x', 700))]),
            poolIds,
            out FrozenDictionary<long, ValidatedPoiRankingItem> items);

        valid.Should().BeTrue();
        items[7L].Reason.Should().HaveLength(500);
        items[7L].AiScore.Should().Be(0.75m);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("below-range")]
    [InlineData("above-range")]
    [InlineData("null-value")]
    [InlineData("null-ranked")]
    public async Task BuildRankingAsync_WithInvalidProviderResponse_FallsBackForWholePool(
        string scenario)
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = (_, _) => Task.FromResult(InvalidResult(scenario)),
        };
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(
            dbContext,
            provider,
            true,
            TieOnlyOptions(maxProviderCandidates: 2),
            logger);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L), Candidate(2L)]),
            CancellationToken.None);

        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.InvalidResponse);
        snapshot.Entries.Values.Should().OnlyContain(entry =>
            entry.EffectiveDesirabilityScore == entry.TripMateBaseScore);
        provider.CallCount.Should().Be(1);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Outcome"].Should().Be("invalid-response");
        log.Properties["FallbackUsed"].Should().Be(true);
        log.Properties["TimedOut"].Should().Be(false);
    }

    [Theory]
    [InlineData(PoiRankingProviderErrorCodes.Quota, "Quota", "quota")]
    [InlineData(PoiRankingProviderErrorCodes.Network, "Network", "network")]
    [InlineData(PoiRankingProviderErrorCodes.ServerError, "ServerError", "server-error")]
    [InlineData(PoiRankingProviderErrorCodes.InvalidResponse, "InvalidResponse", "invalid-response")]
    [InlineData("provider.unknown", "InvalidResponse", "invalid-response")]
    [InlineData("", "InvalidResponse", "invalid-response")]
    public async Task BuildRankingAsync_WithProviderFailure_MapsOutcomeAndUsesBase(
        string errorCode,
        string expectedOutcome,
        string expectedLogOutcome)
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = (_, _) => Task.FromResult(
                Result.Failure<PoiRankingResult>(errorCode, "Provider failure.")),
        };
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(dbContext, provider, true, logger: logger);

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L)]),
            CancellationToken.None);

        snapshot.OutcomeCategory.ToString().Should().Be(expectedOutcome);
        snapshot.Entries[1L].EffectiveDesirabilityScore.Should().Be(
            snapshot.Entries[1L].TripMateBaseScore);
        provider.CallCount.Should().Be(1);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Outcome"].Should().Be(expectedLogOutcome);
        log.Properties["FallbackUsed"].Should().Be(true);
        log.Properties["TimedOut"].Should().Be(false);
    }

    [Fact]
    public async Task BuildRankingAsync_WhenProviderTimesOut_ReturnsTimeoutBaseSnapshot()
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Result.Success(new PoiRankingResult([]));
            },
        };
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(
            dbContext,
            provider,
            true,
            Options(providerTimeout: TimeSpan.FromMilliseconds(20)),
            logger);
        using var callerCancellation = new CancellationTokenSource();

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L)]),
            callerCancellation.Token);

        snapshot.OutcomeCategory.Should().Be(PoiRankingOutcomeCategory.Timeout);
        snapshot.Entries[1L].EffectiveDesirabilityScore.Should().Be(
            snapshot.Entries[1L].TripMateBaseScore);
        provider.CallCount.Should().Be(1);
        callerCancellation.IsCancellationRequested.Should().BeFalse();
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Outcome"].Should().Be("timeout");
        log.Properties["FallbackUsed"].Should().Be(true);
        log.Properties["TimedOut"].Should().Be(true);
    }

    [Fact]
    public async Task BuildRankingAsync_WithPreCancelledCaller_PropagatesCancellation()
    {
        await using var dbContext = CreateDbContext();
        var orchestrator = CreateOrchestrator(dbContext, new RecordingProvider(), true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> action = () => orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L)]),
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BuildRankingAsync_WhenCallerCancelsProviderCall_PropagatesCancellation()
    {
        await using var dbContext = CreateDbContext();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingProvider
        {
            Handler = async (_, cancellationToken) =>
            {
                called.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Result.Success(new PoiRankingResult([]));
            },
        };
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(dbContext, provider, true, logger: logger);
        using var cancellation = new CancellationTokenSource();

        Task<PoiRankingSnapshot> operation = orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L)]),
            cancellation.Token);
        await called.Task;
        cancellation.Cancel();

        await operation.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        provider.CallCount.Should().Be(1);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Information);
        log.Properties["Outcome"].Should().Be("cancelled");
        log.Properties["FallbackUsed"].Should().Be(false);
        log.Properties["TimedOut"].Should().Be(false);
    }

    [Fact]
    public async Task BuildRankingAsync_WithUnexpectedProviderException_Propagates()
    {
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = (_, _) => throw new InvalidOperationException("Unexpected provider defect."),
        };
        var orchestrator = CreateOrchestrator(dbContext, provider, true);

        Func<Task> action = () => orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L)]),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task BuildRankingAsync_LogsOnlyAggregateMetadataWithoutSensitivePayloads()
    {
        const string fakeSecret = "FAKE_SECRET_SHOULD_NOT_APPEAR";
        const string fakeJwt = "FAKE_JWT_SHOULD_NOT_APPEAR";
        const string fakeEmail = "FAKE_EMAIL_SHOULD_NOT_APPEAR";
        const string fakeResponse = "FAKE_RESPONSE_BODY_SHOULD_NOT_APPEAR";
        await using var dbContext = CreateDbContext();
        var provider = new RecordingProvider
        {
            Handler = (_, _) => Task.FromResult(Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.Network,
                $"{fakeSecret} {fakeJwt} {fakeEmail} {fakeResponse}")),
        };
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(dbContext, provider, true, logger: logger);

        await orchestrator.BuildRankingAsync(
            new PoiRankingInput(
                987654L,
                [fakeJwt, fakeEmail],
                [Candidate(1L, name: fakeSecret, tags: [fakeResponse])]),
            CancellationToken.None);

        string renderedLogs = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        renderedLogs.Should()
            .NotContain(fakeSecret)
            .And.NotContain(fakeJwt)
            .And.NotContain(fakeEmail)
            .And.NotContain(fakeResponse)
            .And.NotContain("987654");
    }

    [Fact]
    public async Task BuildRankingAsync_WhenLocalAggregationFails_PropagatesWithoutProviderFallback()
    {
        var dbContext = new Mock<IApplicationDbContext>(MockBehavior.Strict);
        dbContext.SetupGet(context => context.RecommendationBehaviorEvents)
            .Throws(new InvalidOperationException("Local database failure."));
        var provider = new RecordingProvider();
        var logger = new RecordingLogger<PoiRankingOrchestrator>();
        var orchestrator = CreateOrchestrator(dbContext.Object, provider, true, logger: logger);

        Func<Task> action = () => orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], [Candidate(1L)]),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Local database failure.");
        provider.CallCount.Should().Be(0);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildRankingAsync_OwnsInputAndProducesFrozenSnapshotWithoutProviderMetadata()
    {
        await using var dbContext = CreateDbContext();
        var preferences = new List<string> { "culture" };
        var tags = new List<string> { "museum" };
        var candidates = new List<PoiRankingInputCandidate>
        {
            Candidate(
                7L,
                categoryName: "Culture",
                tags: tags,
                scenicScore: 8m,
                photoRating: 7m,
                distance: 1.5m,
                cost: 25m),
        };
        var providerCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingProvider
        {
            Handler = async (request, _) =>
            {
                providerCalled.SetResult();
                await releaseProvider.Task;
                return Result.Success(new PoiRankingResult(
                    request.Candidates.Select(candidate =>
                        new PoiRankingItem(candidate.PoiId, 0.9m, "unused")).ToArray()));
            },
        };
        var orchestrator = CreateOrchestrator(dbContext, provider, true);

        Task<PoiRankingSnapshot> operation = orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, preferences, candidates),
            CancellationToken.None);
        await providerCalled.Task;
        preferences.Add("nature");
        tags.Add("changed");
        candidates.Clear();
        releaseProvider.SetResult();
        PoiRankingSnapshot snapshot = await operation;

        snapshot.ProviderPoolPoiIds.Should().BeAssignableTo<FrozenSet<long>>();
        snapshot.Entries.Should()
            .BeAssignableTo<FrozenDictionary<long, PoiRankingSnapshotEntry>>();
        snapshot.ProviderPoolPoiIds.Should().Equal(7L);
        snapshot.Entries[7L].Should().Be(new PoiRankingSnapshotEntry(
            7L,
            0.6125m,
            0.7275m,
            8m,
            7m,
            1.5m,
            25m));
        provider.LastRequest!.Context.PreferenceTokens.Should().Equal("culture");
        provider.LastRequest.Candidates.Single().TagNames.Should().Equal("museum");
        PublicPropertyNames<PoiRankingSnapshotEntry>().Should().NotContain("AiScore", "Reason");
    }

    [Fact]
    public async Task BuildRankingAsync_WhenAiDisabled_PreservesTopKOfTopNForUnchangedEligibility()
    {
        await using var dbContext = CreateDbContext();
        var candidates = Enumerable.Range(1, 10)
            .Select(id => Candidate(id, scenicScore: id))
            .ToArray();
        var orchestrator = CreateOrchestrator(
            dbContext,
            new RecordingProvider(),
            providerEnabled: false,
            TieOnlyOptions(maxProviderCandidates: 5));

        PoiRankingSnapshot snapshot = await orchestrator.BuildRankingAsync(
            new PoiRankingInput(17L, [], candidates),
            CancellationToken.None);
        long[] finalTopThree = snapshot.Entries.Values
            .OrderByDescending(entry => entry.TripMateBaseScore)
            .ThenByDescending(entry => entry.ScenicScoreForRanking ?? decimal.MinValue)
            .ThenByDescending(entry => entry.PhotoRatingForRanking ?? decimal.MinValue)
            .ThenBy(entry => entry.ExplorationDistanceForRanking)
            .ThenBy(entry => entry.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(entry => entry.PoiId)
            .Take(3)
            .Select(entry => entry.PoiId)
            .ToArray();

        snapshot.ProviderPoolPoiIds.Should().BeEquivalentTo([6L, 7L, 8L, 9L, 10L]);
        finalTopThree.Should().Equal(10L, 9L, 8L);
    }

    public static TheoryData<string> PoolOrderingCases => new()
    {
        "base",
        "scenic",
        "photo",
        "distance",
        "cost",
        "poi-id",
        "null-scenic",
        "null-photo",
        "null-cost",
    };

    private static (
        IReadOnlyCollection<string> PreferenceTokens,
        PoiRankingInputCandidate First,
        PoiRankingInputCandidate Second,
        long ExpectedPoiId,
        bool UseDefaultWeights) PoolOrderingCase(string scenario) => scenario switch
        {
            "base" => (["culture"], Candidate(1L, categoryName: "Culture"), Candidate(2L), 1L, true),
            "scenic" => ([], Candidate(1L, scenicScore: 9m), Candidate(2L, scenicScore: 8m), 1L, false),
            "photo" => ([], Candidate(1L, photoRating: 9m), Candidate(2L, photoRating: 8m), 1L, false),
            "distance" => ([], Candidate(1L, distance: 1m), Candidate(2L, distance: 2m), 1L, false),
            "cost" => ([], Candidate(1L, cost: 1m), Candidate(2L, cost: 2m), 1L, false),
            "poi-id" => ([], Candidate(1L), Candidate(2L), 1L, false),
            "null-scenic" => ([], Candidate(1L, scenicScore: 1m), Candidate(2L, scenicScore: null), 1L, false),
            "null-photo" => ([], Candidate(1L, photoRating: 1m), Candidate(2L, photoRating: null), 1L, false),
            "null-cost" => ([], Candidate(1L, cost: 1m), Candidate(2L, cost: null), 1L, false),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static PoiRankingOrchestrator CreateOrchestrator(
        IApplicationDbContext dbContext,
        IPoiRankingProvider provider,
        bool providerEnabled,
        PersonalizationRankingOptions? options = null,
        ILogger<PoiRankingOrchestrator>? logger = null) =>
        new(
            new PersonalBehaviorFeatureAggregator(dbContext),
            provider,
            options ?? Options(),
            providerEnabled,
            logger ?? new RecordingLogger<PoiRankingOrchestrator>());

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static PersonalizationRankingOptions Options(
        int maxProviderCandidates = 60,
        TimeSpan? providerTimeout = null) =>
        new()
        {
            MaxProviderCandidates = maxProviderCandidates,
            ProviderTimeout = providerTimeout ?? TimeSpan.FromSeconds(5),
        };

    private static PersonalizationRankingOptions TieOnlyOptions(
        int maxProviderCandidates,
        decimal baseWeight = 0.60m,
        decimal aiWeight = 0.40m) =>
        new()
        {
            CategoryAffinityWeight = 0m,
            TagAffinityWeight = 0m,
            BehaviorAffinityWeight = 1m,
            ScenicQualityWeight = 0m,
            PhotoQualityWeight = 0m,
            BaseWeight = baseWeight,
            AiWeight = aiWeight,
            MaxProviderCandidates = maxProviderCandidates,
        };

    private static PoiRankingInputCandidate Candidate(
        long poiId,
        int categoryId = 10,
        string? name = null,
        string categoryName = "Other",
        IReadOnlyCollection<string>? tags = null,
        decimal? scenicScore = 5m,
        decimal? photoRating = 5m,
        decimal distance = 5m,
        decimal? cost = 50m) =>
        new(
            poiId,
            categoryId,
            name ?? $"POI {poiId}",
            categoryName,
            tags ?? [],
            scenicScore,
            photoRating,
            distance,
            cost);

    private static Result<PoiRankingResult> InvalidResult(string scenario) => scenario switch
    {
        "duplicate" => Result.Success(new PoiRankingResult(
            [new PoiRankingItem(1L, 0.4m, null), new PoiRankingItem(1L, 0.5m, null)])),
        "unknown" => Result.Success(new PoiRankingResult(
            [new PoiRankingItem(1L, 0.4m, null), new PoiRankingItem(99L, 0.5m, null)])),
        "missing" => Result.Success(new PoiRankingResult(
            [new PoiRankingItem(1L, 0.4m, null)])),
        "extra" => Result.Success(new PoiRankingResult(
            [
                new PoiRankingItem(1L, 0.4m, null),
                new PoiRankingItem(2L, 0.5m, null),
                new PoiRankingItem(99L, 0.6m, null),
            ])),
        "below-range" => Result.Success(new PoiRankingResult(
            [new PoiRankingItem(1L, 0.4m, null), new PoiRankingItem(2L, -0.1m, null)])),
        "above-range" => Result.Success(new PoiRankingResult(
            [new PoiRankingItem(1L, 0.4m, null), new PoiRankingItem(2L, 1.1m, null)])),
        "null-value" => Result.Success<PoiRankingResult>(null!),
        "null-ranked" => Result.Success(new PoiRankingResult(null!)),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static string[] PublicPropertyNames<T>() =>
        typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

    private sealed class RecordingProvider : IPoiRankingProvider
    {
        public Func<
            PoiRankingRequest,
            CancellationToken,
            Task<Result<PoiRankingResult>>>? Handler
        { get; init; }

        public int CallCount { get; private set; }

        public PoiRankingRequest? LastRequest { get; private set; }

        public Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Handler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(Result.Success(new PoiRankingResult(
                    request.Candidates
                        .Select(candidate => new PoiRankingItem(candidate.PoiId, 0.5m, null))
                        .ToArray())));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), properties));
        }
    }
}