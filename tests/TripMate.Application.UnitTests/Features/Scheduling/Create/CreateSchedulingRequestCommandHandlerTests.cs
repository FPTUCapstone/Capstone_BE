using System.Diagnostics.Metrics;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.Features.Scheduling.Diagnostics;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Scheduling.Create;

[Collection("SchedulingFunnelTelemetry")]
public sealed class CreateSchedulingRequestCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new()
    {
        UtcNow = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Handle_ExplanationProviderSucceeds_PersistsAndReturnsValidatedText()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            BeforeCall = () => dbContext.IsExecutingSerializableTransaction.Should().BeFalse(),
            ResultFactory = input => Result.Success(new ItineraryExplanationResult(
                input.Items.Select(item => new ItineraryExplanationItemResult(
                    item.SequenceNo,
                    item.PoiId,
                    $"Giải thích cho mục {item.SequenceNo}.")).ToArray())),
        };
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };
        int savesBeforeHandle = dbContext.SaveChangesAsyncCallCount;

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions())
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        explanationProvider.CallCount.Should().Be(1);
        dbContext.SaveChangesAsyncCallCount.Should().Be(savesBeforeHandle + 3);
        result.Value.Items.Should().OnlyContain(item =>
            item.FriendlyExplanation == $"Giải thích cho mục {item.SequenceNo}.");
        ItineraryItem persisted = await dbContext.ItineraryItems.SingleAsync();
        persisted.FriendlyExplanation.Should().Be("Giải thích cho mục 1.");
        persisted.RecommendationReason.Should().Be(
            result.Value.Items.Single().RecommendationReason);
    }

    [Fact]
    public async Task Handle_ExplanationProviderDisabled_PersistsAndReturnsFallbackWithoutCallingProvider()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var explanationProvider = new RecordingItineraryExplanationProvider();
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationLogger: logger)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        explanationProvider.CallCount.Should().Be(0);
        string fallback = result.Value.Items.Should().ContainSingle().Subject.FriendlyExplanation!;
        fallback.Should().NotBeNullOrWhiteSpace().And.Contain(mandatoryPoi.Name);
        (await dbContext.ItineraryItems.SingleAsync()).FriendlyExplanation.Should().Be(fallback);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Information);
        log.Properties["Outcome"].Should().Be("provider-skipped");
    }

    [Theory]
    [InlineData(ExplanationProviderErrorCodes.Network, "network")]
    [InlineData(ExplanationProviderErrorCodes.Quota, "quota")]
    [InlineData(ExplanationProviderErrorCodes.ServerError, "server-error")]
    [InlineData(ExplanationProviderErrorCodes.InvalidResponse, "invalid-response")]
    public async Task Handle_ExplanationProviderFailure_UsesWholeItineraryFallback(
        string errorCode,
        string expectedOutcome)
    {
        await using var dbContext = TestDbContext.Create();
        await using var disabledContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var disabledPoi = await SeedSelectablePoiAsync(disabledContext);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = _ => Result.Failure<ItineraryExplanationResult>(
                errorCode,
                "api-key-sentinel provider-request-payload-sentinel"),
        };
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        Guid key = Guid.NewGuid();

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions(),
                explanationLogger: logger)
            .Handle(CreateCommand(key) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);
        var disabled = await CreateHandler(
                disabledContext,
                explanationProvider: new RecordingItineraryExplanationProvider())
            .Handle(CreateCommand(key) with
            {
                MandatoryPoiIds = [disabledPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        disabled.IsSuccess.Should().BeTrue();
        explanationProvider.CallCount.Should().Be(1);
        result.Value.Items.Select(SchedulingInvariant).Should().Equal(
            disabled.Value.Items.Select(SchedulingInvariant));
        string fallback = result.Value.Items.Should().ContainSingle().Subject.FriendlyExplanation!;
        fallback.Should().NotBeNullOrWhiteSpace().And.Contain(mandatoryPoi.Name);
        (await dbContext.ItineraryItems.SingleAsync()).FriendlyExplanation.Should().Be(fallback);
        disabled.Value.Items.Single().FriendlyExplanation.Should().Be(fallback);
        LogEntry entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Properties["Outcome"].Should().Be(expectedOutcome);
        entry.Properties["FallbackUsed"].Should().Be(true);
        entry.Properties["TimedOut"].Should().Be(false);
        entry.Message.Should().NotContain("api-key-sentinel")
            .And.NotContain("provider-request-payload-sentinel");
    }

    [Fact]
    public async Task Handle_ExplanationSuccess_LogsOnlySafeOutcomeMetadata()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"api-key-sentinel\",\"provider-request-payload-sentinel\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = input => Result.Success(new ItineraryExplanationResult(
                input.Items.Select(item => new ItineraryExplanationItemResult(
                    item.SequenceNo,
                    item.PoiId,
                    "private-explanation-text")).ToArray())),
        };

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions(),
                explanationLogger: logger)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        LogEntry entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Properties.Keys.Where(key => key != "{OriginalFormat}").Should().BeEquivalentTo(
            "Provider",
            "Enabled",
            "ItemCount",
            "LatencyMs",
            "Outcome",
            "FallbackUsed",
            "TimedOut");
        entry.Properties["Outcome"].Should().Be("success");
        entry.Message.Should().NotContain("private-explanation-text")
            .And.NotContain("api-key-sentinel")
            .And.NotContain("provider-request-payload-sentinel")
            .And.NotContain("42");
    }

    [Fact]
    public async Task Handle_ExplanationProviderTimesOut_UsesFallback()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            AsyncResultFactory = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable after timeout.");
            },
        };
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: new ItineraryExplanationExecutionOptions
                {
                    Enabled = true,
                    ProviderTimeout = TimeSpan.FromMilliseconds(20),
                },
                explanationLogger: logger)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        explanationProvider.CallCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.FriendlyExplanation
            .Should().NotBeNullOrWhiteSpace().And.Contain(mandatoryPoi.Name);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Outcome"].Should().Be("timeout");
        log.Properties["FallbackUsed"].Should().Be(true);
        log.Properties["TimedOut"].Should().Be(true);
    }

    [Fact]
    public async Task Handle_CallerCancelsDuringExplanation_PropagatesCancellation()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var providerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            AsyncResultFactory = async (_, cancellationToken) =>
            {
                providerStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable after cancellation.");
            },
        };
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        using var callerCancellation = new CancellationTokenSource();
        int savesBeforeHandle = dbContext.SaveChangesAsyncCallCount;
        Task<Result<SchedulingResponseDto>> operation = CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: new ItineraryExplanationExecutionOptions
                {
                    Enabled = true,
                    ProviderTimeout = TimeSpan.FromSeconds(30),
                },
                explanationLogger: logger)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, callerCancellation.Token);
        await providerStarted.Task;

        // Caller token is cancelled only AFTER T1 completion (provider was invoked in T2)
        callerCancellation.Cancel();

        // OperationCanceledException propagates
        Func<Task> action = () => operation;
        await action.Should().ThrowAsync<OperationCanceledException>();
        explanationProvider.CallCount.Should().Be(1);

        // Cancellation is NOT converted into timeout, fallback, or invalid-response
        logger.Entries.Should().BeEmpty();

        // SchedulingRequest remains Completed in committed T1 transaction
        dbContext.TransactionExecutionCount.Should().Be(2);
        var persistedRequest = await dbContext.SchedulingRequests.SingleAsync();
        persistedRequest.Status.Should().Be(SchedulingRequestStatus.Completed);

        // Itinerary remains persisted (no scheduling rollback)
        var persistedItinerary = await dbContext.Itineraries.Include(i => i.Items).SingleAsync();
        persistedItinerary.Should().NotBeNull();
        persistedItinerary.Items.Should().HaveCount(1);

        // FriendlyExplanation remains NULL; no fallback persistence occurred
        persistedItinerary.Items.Single().FriendlyExplanation.Should().BeNull();
        dbContext.SaveChangesAsyncCallCount.Should().Be(savesBeforeHandle + 2);
    }

    [Fact]
    public async Task Handle_ExplanationPersistenceException_LogsWarningAndDoesNotFailSchedulingResult()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            BeforeCall = () => dbContext.ThrowOnSaveConcurrency = true,
            ResultFactory = input => Result.Success(new ItineraryExplanationResult(
                input.Items.Select(item => new ItineraryExplanationItemResult(
                    item.SequenceNo,
                    item.PoiId,
                    "Generated explanation.")).ToArray())),
        };
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        int savesBeforeHandle = dbContext.SaveChangesAsyncCallCount;

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions(),
                explanationLogger: logger)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        // 1 & 2. T1 SaveChanges succeeded and transaction committed
        dbContext.TransactionExecutionCount.Should().Be(2);
        // 11 & 12. Exactly one T2 persistence attempt occurred; NO retry (T1 = 1 save, T2 = 1 failed save)
        dbContext.SaveChangesAsyncCallCount.Should().Be(savesBeforeHandle + 3);

        // 4. Handler still preserves the successful scheduling result
        result.IsSuccess.Should().BeTrue();
        // 9. Original create result still contains the already-computed explanation
        result.Value.Items.Should().ContainSingle().Which.FriendlyExplanation.Should().Be("Generated explanation.");

        // 13. Failed tracked T2 state is cleared / does not poison later DB reads
        dbContext.ChangeTracker.Entries().Should().BeEmpty();
        dbContext.ThrowOnSaveConcurrency = false;

        // 5. SchedulingRequest remains Completed
        var persistedRequest = await dbContext.SchedulingRequests.AsNoTracking().SingleAsync();
        persistedRequest.Status.Should().Be(SchedulingRequestStatus.Completed);

        // 6. Itinerary remains persisted
        var persistedItinerary = await dbContext.Itineraries.AsNoTracking().Include(i => i.Items).SingleAsync();
        persistedItinerary.Id.Should().Be(result.Value.ItineraryId);
        persistedItinerary.Items.Should().HaveCount(1);

        // 7. Scheduling item values remain unchanged
        var persistedItem = persistedItinerary.Items.Single();
        var responseItem = result.Value.Items.Single();
        persistedItem.PointOfInterestId.Should().Be(responseItem.PoiId);
        persistedItem.SequenceNo.Should().Be(responseItem.SequenceNo);
        persistedItem.PlannedArrivalUtc.Should().Be(responseItem.PlannedArrival);
        persistedItem.PlannedDepartureUtc.Should().Be(responseItem.PlannedDeparture);
        persistedItem.StayDurationMinutes.Should().Be(responseItem.StayDurationMinutes);
        persistedItem.EstimatedCost.Should().Be(responseItem.EstimatedCost);
        persistedItem.IsMandatory.Should().Be(responseItem.IsMandatory);
        persistedItem.TravelDurationToNextMinutes.Should().Be(responseItem.TravelDurationToNextMinutes);
        persistedItem.RecommendationReason.Should().Be(responseItem.RecommendationReason);

        // 8. FriendlyExplanation remains NULL in persisted state
        persistedItem.FriendlyExplanation.Should().BeNull();

        // 10. Outcome persistence-failed is logged at Warning
        logger.Entries.Should().HaveCount(2);
        LogEntry entry = logger.Entries.Last();
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Properties["Outcome"].Should().Be("persistence-failed");
        entry.Properties["FallbackUsed"].Should().Be(false);

        var freshRead = await dbContext.ItineraryItems.AsNoTracking().SingleAsync();
        freshRead.FriendlyExplanation.Should().BeNull();
        freshRead.PointOfInterestId.Should().Be(mandatoryPoi.Id);
    }

    [Fact]
    public async Task Handle_ExplanationValidatorRejectsResult_UsesFallbackForEveryItem()
    {
        await using var dbContext = TestDbContext.Create();
        var firstPoi = await SeedSelectablePoiAsync(dbContext, "First", 16.0471m, 108.2068m);
        var secondPoi = await SeedSelectablePoiAsync(dbContext, "Second", 16.0472m, 108.2069m);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = input => Result.Success(new ItineraryExplanationResult(
                input.Items.Select((item, index) => new ItineraryExplanationItemResult(
                    item.SequenceNo,
                    index == 0 ? item.PoiId : 999_999L,
                    $"Provider text {index}.")).ToArray())),
        };

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions())
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [firstPoi.Id, secondPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2)
            .And.OnlyContain(item => !string.IsNullOrWhiteSpace(item.FriendlyExplanation));
        (await dbContext.ItineraryItems.ToArrayAsync()).Should().OnlyContain(item =>
            !string.IsNullOrWhiteSpace(item.FriendlyExplanation));
    }

    [Fact]
    public async Task Handle_ExplanationInput_MapsAvailableMissingAndPoiLessRestMetadata()
    {
        await using var dbContext = TestDbContext.Create();
        var availablePoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Riverside park",
            16.0471m,
            108.2068m,
            "https://example.com/riverside");
        availablePoi.AddTag(Tag.Create(" River Walk "));
        availablePoi.AddTag(Tag.Create("RIVER WALK"));
        var missingMetadataPoi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Late activation",
            16.0472m,
            108.2069m,
            "https://example.com/late");
        var rankingTrigger = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Ranking trigger",
            16.0473m,
            108.2070m,
            "https://example.com/trigger");
        dbContext.PointsOfInterest.AddRange(availablePoi, missingMetadataPoi, rankingTrigger);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(availablePoi).Property(poi => poi.AverageVisitDurationMinutes)
            .CurrentValue = 120;
        dbContext.Entry(missingMetadataPoi).Property(poi => poi.AverageVisitDurationMinutes)
            .CurrentValue = 120;
        await dbContext.SaveChangesAsync();
        var rankingProvider = new RecordingPoiRankingProvider();
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = ValidExplanationResult,
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions())
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                AvailableMinutes = 480,
                MandatoryPoiIds = [availablePoi.Id, missingMetadataPoi.Id],
                RestPreference = RestPreference.Frequent,
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ItineraryExplanationInput input = explanationProvider.LastInput!;
        ItineraryExplanationItem available = input.Items.Single(item =>
            item.PoiId == availablePoi.Id && item.Kind == ItineraryItemKind.Visit);
        available.PoiName.Should().Be("Riverside park");
        available.CategoryName.Should().Be("Nature");
        available.TagNames.Should().ContainSingle();
        TravelerPreferenceScoring.NormalizePreferenceToken(available.TagNames.Single())
            .Should().Be("river walk");
        ItineraryExplanationItem missing = input.Items.Single(item =>
            item.PoiId == missingMetadataPoi.Id);
        missing.CategoryName.Should().Be("Culture");
        missing.TagNames.Should().BeEmpty();
        ItineraryExplanationItem freeRest = input.Items.First(item =>
            item.Kind == ItineraryItemKind.Rest && item.PoiId is null);
        freeRest.PoiName.Should().BeNull();
        freeRest.CategoryName.Should().BeNull();
        freeRest.TagNames.Should().BeEmpty();
        result.Value.Items.Select(item => item.PoiId)
            .Should().Contain([availablePoi.Id, missingMetadataPoi.Id]);
    }

    [Fact]
    public async Task Handle_InfeasibleScheduling_DoesNotCallExplanationProvider()
    {
        await using var dbContext = TestDbContext.Create();
        var explanationProvider = new RecordingItineraryExplanationProvider();

        var result = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions())
            .Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        explanationProvider.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(RouteDurationProviderFailureKind.Timeout)]
    [InlineData(RouteDurationProviderFailureKind.Unavailable)]
    public async Task Handle_WhenRoutingProviderFails_ReturnsControlledFailureWithoutPartialPersistence(
        RouteDurationProviderFailureKind failureKind)
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        int savesBeforeHandle = dbContext.SaveChangesAsyncCallCount;

        var result = await CreateHandler(
                dbContext,
                routeDurationProvider: new FailingRouteDurationProvider(failureKind),
                explanationLogger: logger)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.RoutingProviderUnavailable);
        result.ErrorMessage.Should().Be("The routing service is temporarily unavailable. Please try again.");
        dbContext.SaveChangesAsyncCallCount.Should().Be(savesBeforeHandle + 2);
        var releasedRequest = await dbContext.SchedulingRequests.SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
        (await dbContext.ItineraryItems.CountAsync()).Should().Be(0);
        dbContext.ClearTrackedEntities();
        dbContext.ChangeTracker.Entries().Should().BeEmpty();
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Provider"].Should().Be("OpenRouteService");
        log.Properties["Operation"].Should().Be("duration-matrix");
        log.Properties["Category"].Should().Be(failureKind);
    }

    [Fact]
    public async Task Handle_RoutingFailureWithCallerCancellation_ReleasesReservationAndPropagatesCancellation()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        using var callerCancellation = new CancellationTokenSource();
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: new CallerCancelingFailingRouteDurationProvider(callerCancellation));

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var releasedRequest = await dbContext.SchedulingRequests.SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        releasedRequest.GenerationLeaseExpiresAtUtc.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_UnexpectedPreparationFailure_ReleasesReservationAndRethrowsOriginalException()
    {
        using var metrics = new FunnelMetricCapture();
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var originalException = new InvalidOperationException("unexpected preparation failure");
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: new UnexpectedFailureRouteDurationProvider(originalException));

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which
            .Should().BeSameAs(originalException);
        var releasedRequest = await dbContext.SchedulingRequests.SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        releasedRequest.GenerationLeaseExpiresAtUtc.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
        metrics.Attempts.Should().ContainSingle()
            .Which.Should().Be("initial:unexpected_failure");
    }

    [Fact]
    public async Task Handle_NonCallerOperationCanceledException_ReleasesReservationAndRethrowsOriginalException()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var originalException = new OperationCanceledException("provider canceled independently");
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: new UnexpectedFailureRouteDurationProvider(originalException));

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which
            .Should().BeSameAs(originalException);
        var releasedRequest = await dbContext.SchedulingRequests.SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        releasedRequest.GenerationLeaseExpiresAtUtc.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_CallerCancellationDuringPreparation_ReleasesReservationAndRethrowsCancellation()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        using var callerCancellation = new CancellationTokenSource();
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: new CallerCancelingRouteDurationProvider(callerCancellation));

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var releasedRequest = await dbContext.SchedulingRequests.SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        releasedRequest.GenerationLeaseExpiresAtUtc.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_UnexpectedPreparationFailure_WhenReleaseFails_PreservesOriginalException()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var originalException = new InvalidOperationException("unexpected preparation failure");
        var schedulingLock = new FailOnSecondAcquireSchedulingRequestLock();
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: new UnexpectedFailureRouteDurationProvider(originalException),
            schedulingRequestLock: schedulingLock);

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which
            .Should().BeSameAs(originalException);
        schedulingLock.CallCount.Should().Be(2);
        var leasedRequest = await dbContext.SchedulingRequests.AsNoTracking().SingleAsync();
        leasedRequest.Status.Should().Be(SchedulingRequestStatus.Processing);
        leasedRequest.GenerationOwnerId.Should().NotBeNull();
        leasedRequest.GenerationLeaseExpiresAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_UnexpectedFinalizeFailure_ReleasesReservationAndRethrowsOriginalException()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var originalException = new InvalidOperationException("unexpected finalize failure");
        var schedulingLock = new FailSecondAcquireOnceSchedulingRequestLock(originalException);
        var handler = CreateHandler(
            dbContext,
            schedulingRequestLock: schedulingLock);

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which
            .Should().BeSameAs(originalException);
        schedulingLock.CallCount.Should().Be(3);
        var releasedRequest = await dbContext.SchedulingRequests.AsNoTracking().SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        releasedRequest.GenerationLeaseExpiresAtUtc.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_FinalizeCommitFailure_DoesNotEmitSuccessAndEmitsUnexpectedFailure()
    {
        using var metrics = new FunnelMetricCapture();
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var commitFailure = new InvalidOperationException("finalize commit failure");
        dbContext.SerializableTransactionCompletionFailureFactory = executionCount =>
            executionCount == 2 ? commitFailure : null;
        var handler = CreateHandler(dbContext);

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which
            .Should().BeSameAs(commitFailure);
        metrics.Attempts.Should().ContainSingle()
            .Which.Should().Be("initial:unexpected_failure");
        metrics.Attempts.Should().NotContain("initial:success");
    }

    [Fact]
    public async Task Handle_CallerCancellationDuringFinalize_ReleasesReservationAndRethrowsCancellation()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        using var callerCancellation = new CancellationTokenSource();
        var schedulingLock = new CancelSecondAcquireOnceSchedulingRequestLock(callerCancellation);
        var handler = CreateHandler(
            dbContext,
            schedulingRequestLock: schedulingLock);

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            },
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        schedulingLock.CallCount.Should().Be(3);
        var releasedRequest = await dbContext.SchedulingRequests.AsNoTracking().SingleAsync();
        releasedRequest.Status.Should().Be(SchedulingRequestStatus.Pending);
        releasedRequest.GenerationOwnerId.Should().BeNull();
        releasedRequest.GenerationLeaseExpiresAtUtc.Should().BeNull();
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_AfterTransientRoutingFailure_SameKeyCanBeRetriedSuccessfully()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var routeProvider = new FailOnceRouteDurationProvider();
        var handler = CreateHandler(dbContext, routeDurationProvider: routeProvider);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var retry = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.RoutingProviderUnavailable);
        retry.IsSuccess.Should().BeTrue();
        routeProvider.CallCount.Should().Be(2);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.ItineraryItems.CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Handle_Replay_ReturnsPersistedExplanationWithoutSecondProviderCall()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        await SeedSelectablePoiAsync(dbContext, "Optional riverside", 16.0472m, 108.2069m);
        var routeProvider = new RecordingRouteDurationProvider();
        var rankingProvider = new RecordingPoiRankingProvider();
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = ValidExplanationResult,
        };
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: routeProvider,
            rankingProvider: rankingProvider,
            explanationProvider: explanationProvider,
            explanationOptions: EnabledExplanationOptions());
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var first = await handler.Handle(command, CancellationToken.None);
        int savesAfterFirst = dbContext.SaveChangesAsyncCallCount;
        int schedulingRequestsAfterFirst = await dbContext.SchedulingRequests.CountAsync();
        int itinerariesAfterFirst = await dbContext.Itineraries.CountAsync();
        int itineraryItemsAfterFirst = await dbContext.ItineraryItems.CountAsync();
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(first.Value, options => options.WithStrictOrdering());
        replay.Value.SchedulingRequestId.Should().Be(first.Value.SchedulingRequestId);
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        routeProvider.CallCount.Should().Be(1);
        rankingProvider.CallCount.Should().Be(1);
        explanationProvider.CallCount.Should().Be(1);
        dbContext.SaveChangesAsyncCallCount.Should().Be(savesAfterFirst);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(schedulingRequestsAfterFirst);
        (await dbContext.Itineraries.CountAsync()).Should().Be(itinerariesAfterFirst);
        (await dbContext.ItineraryItems.CountAsync()).Should().Be(itineraryItemsAfterFirst);
        replay.Value.Items.Select(item => item.FriendlyExplanation)
            .Should().Equal(first.Value.Items.Select(item => item.FriendlyExplanation));
    }

    [Fact]
    public async Task Handle_Replay_WhenPersistedFriendlyExplanationIsNull_ReturnsNullWithoutCallingProviderOrBackfilling()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            BeforeCall = () => dbContext.ThrowOnSaveConcurrency = true,
            ResultFactory = ValidExplanationResult,
        };
        var logger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        Guid key = Guid.NewGuid();
        var command = CreateCommand(key) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        // First handle: T2 persistence fails so FriendlyExplanation remains NULL in DB
        var firstResult = await CreateHandler(
                dbContext,
                explanationProvider: explanationProvider,
                explanationOptions: EnabledExplanationOptions(),
                explanationLogger: logger)
            .Handle(command, CancellationToken.None);

        firstResult.IsSuccess.Should().BeTrue();
        explanationProvider.CallCount.Should().Be(1);
        (await dbContext.ItineraryItems.SingleAsync()).FriendlyExplanation.Should().BeNull();

        // Clear concurrency exception flag and prepare replay handler
        dbContext.ThrowOnSaveConcurrency = false;
        var replayProvider = new RecordingItineraryExplanationProvider();
        var replayLogger = new RecordingLogger<CreateSchedulingRequestCommandHandler>();
        int saveCountBeforeReplay = dbContext.SaveChangesAsyncCallCount;

        var replayResult = await CreateHandler(
                dbContext,
                explanationProvider: replayProvider,
                explanationOptions: EnabledExplanationOptions(),
                explanationLogger: replayLogger)
            .Handle(command, CancellationToken.None);

        // 1. return the existing scheduling result
        replayResult.IsSuccess.Should().BeTrue();
        replayResult.Value.ItineraryId.Should().Be(firstResult.Value.ItineraryId);
        // 2. FriendlyExplanation remains null
        replayResult.Value.Items.Should().ContainSingle().Which.FriendlyExplanation.Should().BeNull();
        // 3. explanation provider call count = 0
        replayProvider.CallCount.Should().Be(0);
        // 4. no deterministic fallback is generated
        // 5. no LLM explanation is regenerated
        // 6. persisted row remains unchanged
        (await dbContext.ItineraryItems.SingleAsync()).FriendlyExplanation.Should().BeNull();
        // 7. RecommendationReason remains unchanged
        replayResult.Value.Items.Single().RecommendationReason.Should().Be(
            firstResult.Value.Items.Single().RecommendationReason);
        // 8. no T2 persistence attempt occurs during replay
        dbContext.SaveChangesAsyncCallCount.Should().Be(saveCountBeforeReplay);
        replayLogger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ProviderEnabledAndDisabled_ChangesOnlyFriendlyExplanation()
    {
        await using var enabledContext = TestDbContext.Create();
        await using var disabledContext = TestDbContext.Create();
        var enabledPoi = await SeedSelectablePoiAsync(enabledContext);
        var disabledPoi = await SeedSelectablePoiAsync(disabledContext);
        var enabledProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = ValidExplanationResult,
        };
        var disabledProvider = new RecordingItineraryExplanationProvider();
        Guid key = Guid.NewGuid();

        var enabled = await CreateHandler(
                enabledContext,
                explanationProvider: enabledProvider,
                explanationOptions: EnabledExplanationOptions())
            .Handle(CreateCommand(key) with
            {
                MandatoryPoiIds = [enabledPoi.Id],
            }, CancellationToken.None);
        var disabled = await CreateHandler(
                disabledContext,
                explanationProvider: disabledProvider)
            .Handle(CreateCommand(key) with
            {
                MandatoryPoiIds = [disabledPoi.Id],
            }, CancellationToken.None);

        enabled.IsSuccess.Should().BeTrue();
        disabled.IsSuccess.Should().BeTrue();
        enabled.Value.Items.Select(SchedulingInvariant).Should().Equal(
            disabled.Value.Items.Select(SchedulingInvariant));
        enabled.Value.Items.Select(item => item.FriendlyExplanation).Should().NotEqual(
            disabled.Value.Items.Select(item => item.FriendlyExplanation));
        enabledProvider.CallCount.Should().Be(1);
        disabledProvider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        await SeedSelectablePoiAsync(dbContext, "Dragon Bridge", 16.0615m, 108.2277m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        first.Value.Items.Select(item => item.TravelDurationToNextMinutes)
            .Should().Contain(duration => duration.HasValue);
        replay.Value.Items.Select(item => item.TravelDurationToNextMinutes)
            .Should().Equal(first.Value.Items.Select(item => item.TravelDurationToNextMinutes));
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_InfeasibleEndPoi_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            EndPoiId = 999_999,
            ReturnToStart = false,
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        first.ErrorMessage.Should().Be("The selected ending location is unavailable.");
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        replay.ErrorMessage.Should().Be(first.ErrorMessage);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        var persisted = await dbContext.SchedulingRequests.SingleAsync();
        persisted.Status.Should().Be(SchedulingRequestStatus.Failed);
        persisted.FailureCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_MandatoryPoiOutsideSearchArea_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var outsidePoi = await SeedSelectablePoiAsync(dbContext, "Outside area", 16.3000m, 108.3000m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [outsidePoi.Id],
        };

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_NoSelectablePoi_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_PublicTransit_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            TransportMode = TransportMode.PublicTransit,
        };

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_SameKeyWithDifferentPayload_ReturnsConflict()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var routeProvider = new RecordingRouteDurationProvider();
        var rankingProvider = new RecordingPoiRankingProvider();
        var explanationProvider = new RecordingItineraryExplanationProvider
        {
            ResultFactory = ValidExplanationResult,
        };
        var handler = CreateHandler(
            dbContext,
            routeDurationProvider: routeProvider,
            rankingProvider: rankingProvider,
            explanationProvider: explanationProvider,
            explanationOptions: EnabledExplanationOptions());
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        int routeCallsAfterFirst = routeProvider.CallCount;
        int rankingCallsAfterFirst = rankingProvider.CallCount;
        int explanationCallsAfterFirst = explanationProvider.CallCount;
        var mismatch = await handler.Handle(
            command with { AvailableMinutes = 420 },
            CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(first.Value, options => options.WithStrictOrdering());
        routeProvider.CallCount.Should().Be(routeCallsAfterFirst);
        rankingProvider.CallCount.Should().Be(rankingCallsAfterFirst);
        explanationProvider.CallCount.Should().Be(explanationCallsAfterFirst);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_SameKeyWithDifferentMandatoryPois_ReturnsConflict()
    {
        await using var dbContext = TestDbContext.Create();
        var firstMandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var secondMandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Dragon Bridge",
            16.0615m,
            108.2277m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [firstMandatoryPoi.Id],
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var mismatch = await handler.Handle(
            command with { MandatoryPoiIds = [secondMandatoryPoi.Id] },
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_DifferentKeyWithSamePayload_CreatesIndependentOperations()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);

        var first = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);
        var second = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.SchedulingRequestId.Should().NotBe(first.Value.SchedulingRequestId);
        second.Value.ItineraryId.Should().NotBe(first.Value.ItineraryId);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(2);
        (await dbContext.Itineraries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Handle_DifferentTravelerWithSameKey_CreatesIndependentOperations()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var key = Guid.NewGuid();

        var first = await handler.Handle(CreateCommand(key), CancellationToken.None);
        var second = await handler.Handle(
            CreateCommand(key) with { TravelerUserId = 43 },
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.SchedulingRequestId.Should().NotBe(first.Value.SchedulingRequestId);
        second.Value.ItineraryId.Should().NotBe(first.Value.ItineraryId);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(2);
        (await dbContext.Itineraries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Handle_EquivalentNormalizedCoordinates_ReplaysInsteadOfReturningConflict()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());
        var equivalent = command with
        {
            StartLatitude = 16.0544004m,
            StartLongitude = 108.2022004m,
            ExplorationLatitude = 16.0471004m,
            ExplorationLongitude = 108.2068004m,
            SearchRadiusKm = 10.0004m,
            BudgetVnd = 800_000.0004m,
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(equivalent, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_FourthSuccessfulRequestOnSameLocalDate_IsAllowed()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        var fourth = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        fourth.IsSuccess.Should().BeTrue();
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(4);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(true, "not-json")]
    [InlineData(true, "[\"unknown\"]")]
    public async Task Handle_ProfileWithoutMatchingInterest_PreservesFallbackOrdering(
        bool profileExists,
        string? interestTagsJson)
    {
        await using var dbContext = TestDbContext.Create();
        var (fallbackPoi, _) = await SeedPreferenceRankingPoisAsync(dbContext);
        if (profileExists)
        {
            dbContext.TravelerProfiles.Add(TravelerProfile.Create(
                42,
                interestTagsJson,
                _clock.UtcNow));
            await dbContext.SaveChangesAsync();
        }

        var result = await CreateHandler(dbContext).Handle(
            CreateCommand(Guid.NewGuid()) with { AvailableMinutes = 150 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitPoiIds(result.Value).Should().Equal(fallbackPoi.Id);
    }

    [Fact]
    public async Task Handle_UsesSavedTravelerInterestTagsToRankOptionalPois()
    {
        await using var dbContext = TestDbContext.Create();
        var (_, preferredPoi) = await SeedPreferenceRankingPoisAsync(dbContext);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"culture\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateCommand(Guid.NewGuid()) with { AvailableMinutes = 150 },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // This fixture isolates the positive BaseScore contribution from saved InterestTags;
        // it does not assert universal dominance over behavior, quality, AI, or feasibility.
        VisitPoiIds(result.Value).Should().Equal(preferredPoi.Id);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_WhenTravelerProfileAdded_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var (fallbackPoi, _) = await SeedPreferenceRankingPoisAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with { AvailableMinutes = 150 };

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var originalOrder = VisitPoiIds(first.Value);
        originalOrder.Should().Equal(fallbackPoi.Id);

        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"culture\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var replay = await handler.Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        await AssertSuccessfulReplayAsync(dbContext, first.Value, replay.Value, originalOrder);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_WhenTravelerProfileChanged_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var (fallbackPoi, _) = await SeedPreferenceRankingPoisAsync(dbContext);
        var profile = TravelerProfile.Create(42, "[\"unknown\"]", _clock.UtcNow);
        dbContext.TravelerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with { AvailableMinutes = 150 };

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var originalOrder = VisitPoiIds(first.Value);
        originalOrder.Should().Equal(fallbackPoi.Id);

        dbContext.Entry(profile).Property(item => item.InterestTagsJson).CurrentValue = "[\"culture\"]";
        await dbContext.SaveChangesAsync();

        var replay = await handler.Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        await AssertSuccessfulReplayAsync(dbContext, first.Value, replay.Value, originalOrder);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_WhenTravelerProfileRemoved_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var (_, preferredPoi) = await SeedPreferenceRankingPoisAsync(dbContext);
        var profile = TravelerProfile.Create(42, "[\"culture\"]", _clock.UtcNow);
        dbContext.TravelerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with { AvailableMinutes = 150 };

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var originalOrder = VisitPoiIds(first.Value);
        originalOrder.Should().Equal(preferredPoi.Id);

        dbContext.TravelerProfiles.Remove(profile);
        await dbContext.SaveChangesAsync();

        var replay = await handler.Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        await AssertSuccessfulReplayAsync(dbContext, first.Value, replay.Value, originalOrder);
    }

    [Fact]
    public async Task Handle_MoreThanSixtyEligiblePois_BoundsMatrixAndRetainsMandatoryPois()
    {
        await using var dbContext = TestDbContext.Create();
        var pois = new List<PointOfInterest>();
        for (var index = 0; index < 65; index++)
        {
            pois.Add(await SeedSelectablePoiAsync(
                dbContext,
                $"Candidate {index:D2}",
                16.0471m + (index * 0.0001m),
                108.2068m + (index * 0.0001m)));
        }

        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [pois[^2].Id, pois[^1].Id],
        };

        var result = await CreateHandler(dbContext, routeProvider).Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        routeProvider.LastPointCount.Should().BeLessOrEqualTo(42);
        result.Value.Items.Select(item => item.PoiId)
            .Should().Contain([pois[^2].Id, pois[^1].Id]);
    }

    [Fact]
    public async Task Handle_ReplayPrecheckHit_SkipsRankingAndUsesAuthoritativeReplay()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var rankingProvider = new RecordingPoiRankingProvider();
        var handler = CreateHandler(dbContext, rankingProvider: rankingProvider);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(
            command with { AvailableMinutes = 420 },
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        rankingProvider.CallCount.Should().Be(1);
        dbContext.TransactionExecutionCount.Should().Be(3);
    }

    [Fact]
    public async Task Handle_FirstOwner_CommitsReservationBeforeProvidersAndRunsThemOutsideTransactions()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var optionalPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Optional garden",
            16.0472m,
            108.2069m,
            "https://example.com/garden");
        optionalPoi.AddTag(Tag.Create("River Walk"));
        dbContext.PointsOfInterest.Add(optionalPoi);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(optionalPoi).Property(poi => poi.ScenicScore).CurrentValue = 8.5m;
        dbContext.Entry(optionalPoi).Property(poi => poi.PhotoRating).CurrentValue = 7.5m;
        dbContext.Entry(optionalPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue = 75_000m;
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\" NATURE \" , \"river walk\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var schedulingLock = new RecordingSchedulingRequestLock();
        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                dbContext.TransactionExecutionCount.Should().Be(1);
                schedulingLock.CallCount.Should().Be(1);
                dbContext.IsExecutingSerializableTransaction.Should().BeFalse();
                dbContext.SchedulingRequests.AsNoTracking().Single().Status
                    .Should().Be(SchedulingRequestStatus.Processing);
            },
        };
        var handler = CreateHandler(
            dbContext,
            rankingProvider: rankingProvider,
            schedulingRequestLock: schedulingLock);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        rankingProvider.LastRequest.Should().NotBeNull();
        rankingProvider.LastRequest!.Context.PreferenceTokens.Should()
            .BeEquivalentTo(["nature", "river walk"]);
        rankingProvider.LastRequest.Candidates.Should().ContainSingle();
        rankingProvider.LastRequest.Candidates.Single().Should().BeEquivalentTo(
            new PoiRankingCandidate(
                optionalPoi.Id,
                "Optional garden",
                "Nature",
                ["River Walk"]));
        rankingProvider.LastRequest.Candidates.Should()
            .NotContain(candidate => candidate.PoiId == mandatoryPoi.Id);
        schedulingLock.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_PrecheckMissWithNoOptionalPois_UsesCurrentMandatorySemantics()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var rankingProvider = new RecordingPoiRankingProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await CreateHandler(dbContext, rankingProvider: rankingProvider)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(0);
        result.Value.Items.Select(item => item.PoiId).Should().Contain(mandatoryPoi.Id);
    }

    [Theory]
    [InlineData("scenic")]
    [InlineData("photo")]
    [InlineData("distance")]
    [InlineData("cost")]
    public async Task Handle_PrecheckMiss_MapsRankingTieBreakerScalars(string scenario)
    {
        await using var dbContext = TestDbContext.Create();
        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Preferred candidate",
            16.0472m,
            108.2069m,
            "https://example.com/preferred");
        var otherPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Other candidate",
            scenario == "distance" ? 16.0572m : 16.0473m,
            scenario == "distance" ? 108.2169m : 108.2070m,
            "https://example.com/other");
        dbContext.PointsOfInterest.AddRange(preferredPoi, otherPoi);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(preferredPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario == "scenic" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario == "scenic" ? 8m : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario == "photo" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario == "photo" ? 8m : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario == "cost" ? 50_000m : 60_000m;
        dbContext.Entry(otherPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario == "cost" ? 70_000m : 60_000m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider();
        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.LastRequest!.Candidates.Should().ContainSingle();
        rankingProvider.LastRequest.Candidates.Single().PoiId.Should().Be(preferredPoi.Id);
    }

    [Fact]
    public async Task Handle_ProviderPoolBoundary_ExcludesOutsidePoolWithoutBackfillAndRetainsMandatory()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var pooledPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Pooled garden",
            16.0472m,
            108.2069m,
            "https://example.com/pooled");
        var outsidePoolPois = Enumerable.Range(1, 3)
            .Select(index => CreateSelectablePoi(
                PoiCategory.Create("Culture", null),
                $"Outside pool {index}",
                16.0472m + (index * 0.0001m),
                108.2069m + (index * 0.0001m),
                $"https://example.com/outside-{index}"))
            .ToArray();
        outsidePoolPois[0].AddTag(Tag.Create("Favorite"));
        dbContext.PointsOfInterest.AddRange([pooledPoi, .. outsidePoolPois]);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(pooledPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
        for (var index = 0; index < outsidePoolPois.Length; index++)
        {
            dbContext.Entry(outsidePoolPois[index]).Property(poi => poi.ScenicScore).CurrentValue =
                9m - index;
        }

        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"favorite\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();
        var rankingProvider = new RecordingPoiRankingProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 600,
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        rankingProvider.LastRequest!.Candidates.Select(candidate => candidate.PoiId)
            .Should().Equal(pooledPoi.Id);
        VisitPoiIds(result.Value).Should().Contain([mandatoryPoi.Id, pooledPoi.Id]);
        VisitPoiIds(result.Value).Should()
            .NotContain(outsidePoolPois.Select(poi => (long?)poi.Id));
    }

    [Fact]
    public async Task Handle_MatrixCapacityPrefersEffectiveScoreOverLegacyPreferenceScore()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPois = await SeedMandatoryMatrixPoisAsync(dbContext);
        var higherEffectivePoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Higher effective candidate",
            16.0472m,
            108.2069m,
            "https://example.com/higher-effective");
        var higherLegacyPreferencePoi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Higher legacy preference candidate",
            16.0473m,
            108.2070m,
            "https://example.com/higher-legacy-preference");
        higherLegacyPreferencePoi.AddTag(Tag.Create("Favorite"));
        dbContext.PointsOfInterest.AddRange(higherEffectivePoi, higherLegacyPreferencePoi);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"favorite\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();
        dbContext.Entry(higherEffectivePoi).Property(poi => poi.ScenicScore).CurrentValue = 1m;
        dbContext.Entry(higherLegacyPreferencePoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider
        {
            RankedItemsFactory = request => request.Candidates
                .Select(candidate => new PoiRankingItem(
                    candidate.PoiId,
                    candidate.PoiId == higherEffectivePoi.Id ? 0.9m : 0.1m,
                    null))
                .ToArray(),
        };
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 720,
            MandatoryPoiIds = mandatoryPois.Select(poi => poi.Id).ToArray(),
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: MatrixOrderingOptions(),
                generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 })
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitPoiIds(result.Value).Should().Contain(higherEffectivePoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(higherLegacyPreferencePoi.Id);
        VisitPoiIds(result.Value).Should().Contain(mandatoryPois.Select(poi => (long?)poi.Id));
    }

    [Theory]
    [InlineData("scenic")]
    [InlineData("scenic-null")]
    [InlineData("photo")]
    [InlineData("photo-null")]
    [InlineData("distance")]
    [InlineData("cost")]
    [InlineData("cost-null")]
    [InlineData("poi-id")]
    public async Task Handle_MatrixCapacityUsesFrozenSnapshotTieBreakers(string scenario)
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPois = await SeedMandatoryMatrixPoisAsync(dbContext);
        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Frozen preferred candidate",
            16.0472m,
            108.2069m,
            "https://example.com/frozen-preferred");
        var equalDistance = scenario is "cost" or "cost-null" or "poi-id";
        var otherPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Current preferred candidate",
            scenario == "distance" ? 16.0572m : equalDistance ? 16.0472m : 16.0473m,
            scenario == "distance" ? 108.2169m : equalDistance ? 108.2069m : 108.2070m,
            "https://example.com/current-preferred");
        dbContext.PointsOfInterest.AddRange(preferredPoi, otherPoi);
        await dbContext.SaveChangesAsync();

        dbContext.Entry(preferredPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario is "scenic" or "scenic-null" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario == "scenic" ? 8m : scenario == "scenic-null" ? null : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario is "photo" or "photo-null" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario == "photo" ? 8m : scenario == "photo-null" ? null : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario is "cost" or "cost-null" ? 50_000m : 60_000m;
        dbContext.Entry(otherPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario == "cost" ? 70_000m : scenario == "cost-null" ? null : 60_000m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                switch (scenario)
                {
                    case "scenic":
                    case "scenic-null":
                        dbContext.Entry(preferredPoi).Property(poi => poi.ScenicScore).CurrentValue = null;
                        dbContext.Entry(otherPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
                        break;
                    case "photo":
                    case "photo-null":
                        dbContext.Entry(preferredPoi).Property(poi => poi.PhotoRating).CurrentValue = null;
                        dbContext.Entry(otherPoi).Property(poi => poi.PhotoRating).CurrentValue = 10m;
                        break;
                    case "distance":
                        dbContext.Entry(preferredPoi).Property(poi => poi.Latitude).CurrentValue = 16.08m;
                        dbContext.Entry(otherPoi).Property(poi => poi.Latitude).CurrentValue = 16.04715m;
                        break;
                    case "cost":
                    case "cost-null":
                        dbContext.Entry(preferredPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue = null;
                        dbContext.Entry(otherPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue = 10_000m;
                        break;
                    case "poi-id":
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }

                dbContext.SaveChanges();
            },
        };
        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 720,
            BudgetVnd = null,
            MandatoryPoiIds = mandatoryPois.Select(poi => poi.Id).ToArray(),
        };

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                rankingOptions: MatrixOrderingOptions(),
                generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 })
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        routeProvider.LastPointCount.Should().Be(8);
        VisitPoiIds(result.Value).Should().Contain(preferredPoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(otherPoi.Id);
    }

    [Fact]
    public async Task Handle_AiDisabledMatrixPreselectionMatchesFrozenBaseTopK()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPois = await SeedMandatoryMatrixPoisAsync(dbContext);
        var optionals = Enumerable.Range(0, 3)
            .Select(index => CreateSelectablePoi(
                PoiCategory.Create("Nature", null),
                $"Base candidate {index}",
                16.0472m + (index * 0.0001m),
                108.2069m + (index * 0.0001m),
                $"https://example.com/base-{index}"))
            .ToArray();
        dbContext.PointsOfInterest.AddRange(optionals);
        await dbContext.SaveChangesAsync();
        for (var index = 0; index < optionals.Length; index++)
        {
            dbContext.Entry(optionals[index]).Property(poi => poi.ScenicScore).CurrentValue =
                10m - index;
        }

        await dbContext.SaveChangesAsync();
        var rankingProvider = new RecordingPoiRankingProvider();
        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 720,
            MandatoryPoiIds = mandatoryPois.Select(poi => poi.Id).ToArray(),
        };

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                rankingOptions: BaseOnlyScenicOptions(),
                generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 },
                providerEnabled: false)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(0);
        routeProvider.LastPoints.Should().Contain(new RoutePoint(
            optionals[0].Latitude,
            optionals[0].Longitude));
        routeProvider.LastPoints.Should().NotContain(new RoutePoint(
            optionals[1].Latitude,
            optionals[1].Longitude));
        routeProvider.LastPoints.Should().NotContain(new RoutePoint(
            optionals[2].Latitude,
            optionals[2].Longitude));
    }

    [Fact]
    public void ToCandidate_OptionalUsesFrozenScoresAndCurrentFeasibilityValues()
    {
        var poi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Current optional",
            16.0472m,
            108.2069m,
            "https://example.com/current-optional");
        poi.AddTag(Tag.Create("Favorite"));
        using var currentContext = TestDbContext.Create();
        currentContext.PointsOfInterest.Add(poi);
        currentContext.SaveChanges();
        currentContext.Entry(poi).Property(item => item.ScenicScore).CurrentValue = 1m;
        currentContext.Entry(poi).Property(item => item.PhotoRating).CurrentValue = 2m;
        currentContext.Entry(poi).Property(item => item.EstimatedVisitCost).CurrentValue = 200_000m;
        var frozenEntry = new PoiRankingSnapshotEntry(
            poi.Id,
            TripMateBaseScore: 0.321m,
            EffectiveDesirabilityScore: 0.654m,
            ScenicScoreForRanking: 9m,
            PhotoRatingForRanking: 8m,
            ExplorationDistanceForRanking: 1.25m,
            EstimatedVisitCostForRanking: 50_000m);

        GenerationCandidate candidate = CreateSchedulingRequestCommandHandler.ToCandidate(
            poi,
            new HashSet<string>(["favorite"]),
            frozenEntry);

        candidate.TripMateBaseScore.Should().Be(0.321m);
        candidate.EffectiveDesirabilityScore.Should().Be(0.654m);
        candidate.ScenicScoreForRanking.Should().Be(9m);
        candidate.PhotoRatingForRanking.Should().Be(8m);
        candidate.EstimatedVisitCostForRanking.Should().Be(50_000m);
        candidate.PreferenceScore.Should().Be(100);
        candidate.ScenicScore.Should().Be(1m);
        candidate.PhotoRating.Should().Be(2m);
        candidate.EstimatedVisitCost.Should().Be(200_000m);
    }

    [Fact]
    public void ToCandidate_MandatoryUsesExplicitNeutralScores()
    {
        var poi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Mandatory candidate",
            16.0471m,
            108.2068m,
            "https://example.com/mandatory");

        GenerationCandidate candidate = CreateSchedulingRequestCommandHandler.ToCandidate(
            poi,
            new HashSet<string>(),
            rankingEntry: null);

        candidate.TripMateBaseScore.Should().Be(0m);
        candidate.EffectiveDesirabilityScore.Should().Be(0m);
        candidate.ScenicScoreForRanking.Should().BeNull();
        candidate.PhotoRatingForRanking.Should().BeNull();
        candidate.EstimatedVisitCostForRanking.Should().BeNull();
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("not-planning-ready")]
    [InlineData("outside-radius")]
    [InlineData("missing")]
    public async Task Handle_GenerationSnapshotChanges_RegeneratesOnceFromCurrentData(
        string invalidation)
    {
        using var metrics = new FunnelMetricCapture();

        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var pooledPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Pooled candidate",
            16.0472m,
            108.2069m);
        var outsidePoolPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Outside-pool backfill candidate",
            16.0473m,
            108.2070m);
        dbContext.Entry(pooledPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
        dbContext.Entry(outsidePoolPoi).Property(poi => poi.ScenicScore).CurrentValue = 9m;
        await dbContext.SaveChangesAsync();

        var invalidationCount = 0;
        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                invalidationCount++;
                if (invalidationCount > 1)
                {
                    return;
                }

                switch (invalidation)
                {
                    case "inactive":
                        dbContext.Entry(pooledPoi).Property(poi => poi.Status).CurrentValue =
                            PointOfInterestStatus.Inactive;
                        dbContext.Entry(pooledPoi).Property(poi => poi.Status).IsModified = true;
                        break;
                    case "not-planning-ready":
                        dbContext.Entry(pooledPoi).Property(poi => poi.SourceUrl).CurrentValue = null;
                        dbContext.Entry(pooledPoi).Property(poi => poi.SourceUrl).IsModified = true;
                        break;
                    case "outside-radius":
                        dbContext.Entry(pooledPoi).Property(poi => poi.Latitude).CurrentValue = 17m;
                        dbContext.Entry(pooledPoi).Property(poi => poi.Latitude).IsModified = true;
                        break;
                    case "missing":
                        dbContext.PointsOfInterest.Remove(pooledPoi);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(invalidation));
                }

                dbContext.SaveChanges();
            },
        };
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 600,
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        metrics.Attempts.Should().ContainInOrder(
            "initial:snapshot_mismatch_retryable",
            "snapshot_retry:success");
        metrics.FinalizeCounts.Should().Contain(item =>
            item.Attempt == "initial" && item.Value == 0);
        VisitPoiIds(result.Value).Should().Contain(mandatoryPoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(pooledPoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(outsidePoolPoi.Id);
    }

    [Fact]
    public async Task Handle_MetadataOnlySnapshotChange_RetriesWithoutChangingValidPoolObservation()
    {
        using var metrics = new FunnelMetricCapture();
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var optionalPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Optional museum",
            16.0472m,
            108.2069m);
        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                dbContext.Entry(optionalPoi).Property(poi => poi.Name).CurrentValue =
                    "Renamed optional museum";
                dbContext.Entry(optionalPoi).Property(poi => poi.Name).IsModified = true;
                dbContext.SaveChanges();
            },
        };
        var routeProvider = new RecordingRouteDurationProvider();

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        routeProvider.CallCount.Should().Be(2);
        metrics.Attempts.Should().ContainInOrder(
            "initial:snapshot_mismatch_retryable",
            "snapshot_retry:success");
        metrics.FinalizeCounts.Should().Contain(item =>
            item.Attempt == "initial" && item.Value == 1);
        VisitPoiIds(result.Value).Should().Contain(optionalPoi.Id);
    }

    [Fact]
    public async Task Handle_BehaviorSnapshotChangesOnce_RetriesWithCurrentBehaviorAndFrozenRanking()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var optionalPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Optional museum",
            16.0472m,
            108.2069m);
        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                dbContext.RecommendationBehaviorEvents.Add(RecommendationBehaviorEvent.Create(
                    travelerUserId: 42,
                    poiId: optionalPoi.Id,
                    itineraryId: null,
                    RecommendationEventType.Like,
                    originalPosition: null,
                    newPosition: null,
                    wasMandatory: null,
                    RecommendationCaptureSource.PoiDetail,
                    _clock.UtcNow,
                    Guid.NewGuid()));
                dbContext.SaveChanges();
            },
        };
        var routeProvider = new RecordingRouteDurationProvider();
        var rateLimiter = new CountingAllowingGenerateRateLimiter();

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                generateRateLimiter: rateLimiter)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        routeProvider.CallCount.Should().Be(2);
        rateLimiter.CallCount.Should().Be(1);
        VisitPoiIds(result.Value).Should().Contain(optionalPoi.Id);
    }

    [Fact]
    public async Task Handle_DistantPoiMutationOutsideBoundingBox_DoesNotTriggerSnapshotRetry()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        await SeedSelectablePoiAsync(
            dbContext,
            "Optional attraction",
            16.0472m,
            108.2069m);
        var distantPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Distant Hanoi attraction",
            21.0285m,
            105.8542m);
        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                dbContext.Entry(distantPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
                dbContext.Entry(distantPoi).Property(poi => poi.ScenicScore).IsModified = true;
                dbContext.SaveChanges();
            },
        };
        var routeProvider = new RecordingRouteDurationProvider();

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                AvailableMinutes = 600,
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        routeProvider.CallCount.Should().Be(1);
        VisitPoiIds(result.Value).Should().Contain(mandatoryPoi.Id);
    }

    [Fact]
    public async Task Handle_MandatoryPoiOutsideBoundingBox_IsLoadedAndReturnsControlledInfeasible()
    {
        await using var dbContext = TestDbContext.Create();
        var distantMandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Far away mandatory attraction",
            21.0285m,
            105.8542m);

        var result = await CreateHandler(dbContext)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [distantMandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        result.ErrorMessage.Should().Be(
            "A mandatory location is unavailable or outside the selected area.");
    }

    [Fact]
    public async Task Handle_ActiveEndPoiOutsideBoundingBox_RemainsUsable()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var distantEndPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Distant custom end",
            21.0285m,
            105.8542m);
        var routeProvider = new RecordingRouteDurationProvider();

        var result = await CreateHandler(dbContext, routeProvider)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
                EndPoiId = distantEndPoi.Id,
                ReturnToStart = false,
                AvailableMinutes = 600,
            }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        routeProvider.LastPoints.Should().Contain(
            new RoutePoint(distantEndPoi.Latitude, distantEndPoi.Longitude));
    }

    [Fact]
    public async Task Handle_GenerationSnapshotChangesTwice_ReleasesReservationAndReturnsRetryableFailure()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var changingPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Changing optional",
            16.0472m,
            108.2069m);
        var routeProvider = new RecordingRouteDurationProvider
        {
            BeforeReturn = () =>
            {
                var entry = dbContext.Entry(changingPoi).Property(poi => poi.ScenicScore);
                entry.CurrentValue = (entry.CurrentValue ?? 0m) + 1m;
                entry.IsModified = true;
                dbContext.SaveChanges();
            },
        };

        var rankingProvider = new RecordingPoiRankingProvider();
        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider)
            .Handle(CreateCommand(Guid.NewGuid()) with
            {
                MandatoryPoiIds = [mandatoryPoi.Id],
            }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationTemporarilyUnavailable);
        rankingProvider.CallCount.Should().Be(1);
        routeProvider.CallCount.Should().Be(2);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
        var request = await dbContext.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Pending);
        request.GenerationOwnerId.Should().BeNull();
        request.GenerationLeaseExpiresAtUtc.Should().BeNull();
        request.GenerationAttempt.Should().Be(1);
    }

    [Fact]
    public async Task Handle_RateLimitRejected_RemovesNewReservationWithoutProviderWork()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var rankingProvider = new RecordingPoiRankingProvider();
        var routeProvider = new RecordingRouteDurationProvider();

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                generateRateLimiter: new RejectingGenerateRateLimiter())
            .Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);
        rankingProvider.CallCount.Should().Be(0);
        routeProvider.CallCount.Should().Be(0);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(0);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_RateLimiterUnavailable_RemovesNewReservationWithoutProviderWork()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var rankingProvider = new RecordingPoiRankingProvider();
        var routeProvider = new RecordingRouteDurationProvider();

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                generateRateLimiter: new RejectingGenerateRateLimiter(
                    SchedulingErrorCodes.GenerationRateLimiterUnavailable,
                    retryAfterSeconds: 0))
            .Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
        result.ErrorMessage.Should().Be(
            "Itinerary generation is temporarily unavailable. Please try again.");
        result.ErrorMetadata.Should().BeEmpty();
        rankingProvider.CallCount.Should().Be(0);
        routeProvider.CallCount.Should().Be(0);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(0);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_RateLimitRejected_PreservesPreExistingPendingReservation()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };
        var initialResult = await CreateHandler(
                dbContext,
                routeDurationProvider: new FailingRouteDurationProvider(
                    RouteDurationProviderFailureKind.Timeout))
            .Handle(command, CancellationToken.None);
        initialResult.ErrorCode.Should().Be(SchedulingErrorCodes.RoutingProviderUnavailable);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);

        var rejectedResult = await CreateHandler(
                dbContext,
                generateRateLimiter: new RejectingGenerateRateLimiter())
            .Handle(command, CancellationToken.None);

        rejectedResult.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);
        var request = await dbContext.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Pending);
        request.GenerationOwnerId.Should().BeNull();
        request.GenerationLeaseExpiresAtUtc.Should().BeNull();
        request.GenerationAttempt.Should().Be(2);
    }

    [Fact]
    public async Task Handle_RateLimitRejectedWithCallerCancellation_ReleasesReservationAndPropagatesCancellation()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        using var callerCancellation = new CancellationTokenSource();
        var rankingProvider = new RecordingPoiRankingProvider();
        var routeProvider = new RecordingRouteDurationProvider();
        var handler = CreateHandler(
            dbContext,
            routeProvider,
            rankingProvider,
            generateRateLimiter: new CallerCancelingRejectingGenerateRateLimiter(callerCancellation));

        Func<Task> act = async () => await handler.Handle(
            CreateCommand(Guid.NewGuid()),
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rankingProvider.CallCount.Should().Be(0);
        routeProvider.CallCount.Should().Be(0);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(0);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    private CreateSchedulingRequestCommandHandler CreateHandler(
        TestDbContext dbContext,
        IRouteDurationProvider? routeDurationProvider = null,
        RecordingPoiRankingProvider? rankingProvider = null,
        ISchedulingRequestLock? schedulingRequestLock = null,
        PersonalizationRankingOptions? rankingOptions = null,
        SchedulingGenerationOptions? generationOptions = null,
        bool providerEnabled = true,
        IItineraryExplanationProvider? explanationProvider = null,
        ItineraryExplanationExecutionOptions? explanationOptions = null,
        ILogger<CreateSchedulingRequestCommandHandler>? explanationLogger = null,
        IGenerateRateLimiter? generateRateLimiter = null,
        SchedulingReservationOptions? reservationOptions = null)
    {
        return new(
            dbContext,
            _clock,
            routeDurationProvider ?? new FixedRouteDurationProvider(),
            schedulingRequestLock ?? new NoOpSchedulingRequestLock(),
            CreateRankingOrchestrator(
                dbContext,
                rankingProvider ?? new RecordingPoiRankingProvider(),
                rankingOptions,
                providerEnabled),
            generationOptions,
            explanationProvider,
            explanationOptions,
            explanationLogger,
            generateRateLimiter,
            reservationOptions);
    }

    private static ItineraryExplanationExecutionOptions EnabledExplanationOptions() =>
        new() { Enabled = true };

    private static PoiRankingOrchestrator CreateRankingOrchestrator(
        IApplicationDbContext dbContext,
        IPoiRankingProvider rankingProvider,
        PersonalizationRankingOptions? rankingOptions,
        bool providerEnabled = true)
    {
        return new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(dbContext),
            rankingProvider,
            rankingOptions ?? new PersonalizationRankingOptions(),
            providerEnabled,
            NullLogger<PoiRankingOrchestrator>.Instance);
    }

    private static PersonalizationRankingOptions TieBreakerOnlyOptions() => new()
    {
        CategoryAffinityWeight = 0m,
        TagAffinityWeight = 0m,
        BehaviorAffinityWeight = 1m,
        ScenicQualityWeight = 0m,
        PhotoQualityWeight = 0m,
        MaxProviderCandidates = 1,
    };

    private static PersonalizationRankingOptions MatrixOrderingOptions() => new()
    {
        BaseWeight = 0m,
        AiWeight = 1m,
        MaxProviderCandidates = 2,
    };

    private static PersonalizationRankingOptions BaseOnlyScenicOptions() => new()
    {
        CategoryAffinityWeight = 0m,
        TagAffinityWeight = 0m,
        BehaviorAffinityWeight = 0m,
        ScenicQualityWeight = 1m,
        PhotoQualityWeight = 0m,
        MaxProviderCandidates = 2,
    };

    private static async Task<IReadOnlyList<PointOfInterest>> SeedMandatoryMatrixPoisAsync(
        TestDbContext dbContext)
    {
        var mandatoryPois = new List<PointOfInterest>();
        for (var index = 0; index < 5; index++)
        {
            mandatoryPois.Add(await SeedSelectablePoiAsync(
                dbContext,
                $"Mandatory {index}",
                16.0471m + (index * 0.00001m),
                108.2068m + (index * 0.00001m)));
        }

        return mandatoryPois;
    }

    private static Task<PointOfInterest> SeedSelectablePoiAsync(TestDbContext dbContext) =>
        SeedSelectablePoiAsync(dbContext, "Cham Museum", 16.0471m, 108.2068m);

    private static async Task<PointOfInterest> SeedSelectablePoiAsync(
        TestDbContext dbContext,
        string name,
        decimal latitude,
        decimal longitude)
    {
        var category = PoiCategory.Create("Culture", null);
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", DateTimeOffset.UtcNow);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }

    private async Task<(PointOfInterest FallbackPoi, PointOfInterest PreferredPoi)>
        SeedPreferenceRankingPoisAsync(TestDbContext dbContext)
    {
        var fallbackPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Fallback attraction",
            16.0471m,
            108.2068m,
            "https://example.com/fallback");
        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Preferred museum",
            16.0472m,
            108.2069m,
            "https://example.com/preferred");

        dbContext.PointsOfInterest.AddRange(fallbackPoi, preferredPoi);
        await dbContext.SaveChangesAsync();
        return (fallbackPoi, preferredPoi);
    }

    private PointOfInterest CreateSelectablePoi(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude,
        string sourceUrl)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            _clock.UtcNow,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, sourceUrl, _clock.UtcNow);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        return poi;
    }

    private static long?[] VisitPoiIds(SchedulingResponseDto response) =>
        response.Items
            .Where(item => item.ItemKind == ItineraryItemKind.Visit)
            .Select(item => item.PoiId)
            .ToArray();

    private static Result<ItineraryExplanationResult> ValidExplanationResult(
        ItineraryExplanationInput input) =>
        Result.Success(new ItineraryExplanationResult(
            input.Items.Select(item => new ItineraryExplanationItemResult(
                item.SequenceNo,
                item.PoiId,
                $"Nội dung AI cho mục {item.SequenceNo}.")).ToArray()));

    private static SchedulingInvariantSnapshot SchedulingInvariant(SchedulingItemDto item) =>
        new(
            item.SequenceNo,
            item.PoiId,
            item.ItemKind,
            item.PlannedArrival,
            item.PlannedDeparture,
            item.StayDurationMinutes,
            item.TravelDurationToNextMinutes,
            item.EstimatedCost,
            item.IsMandatory,
            item.RecommendationReason);

    private static async Task AssertSuccessfulReplayAsync(
        TestDbContext dbContext,
        SchedulingResponseDto first,
        SchedulingResponseDto replay,
        IReadOnlyCollection<long?> originalOrder)
    {
        replay.ItineraryId.Should().Be(first.ItineraryId);
        VisitPoiIds(replay).Should().Equal(originalOrder);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    private static CreateSchedulingRequestCommand CreateCommand(Guid key) => new(
        TravelerUserId: 42,
        IdempotencyKey: key,
        StartAt: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        TimeZoneId: "Asia/Ho_Chi_Minh",
        StartLatitude: 16.0544m,
        StartLongitude: 108.2022m,
        ExplorationLatitude: 16.0471m,
        ExplorationLongitude: 108.2068m,
        EndPoiId: null,
        ReturnToStart: true,
        AvailableMinutes: 480,
        TransportMode: TransportMode.Motorbike,
        SearchRadiusKm: 10m,
        BudgetVnd: 800_000m,
        MandatoryPoiIds: [],
        RestPreference: RestPreference.None);

    private static async Task AssertInfeasibleReplayAsync(
        CreateSchedulingRequestCommandHandler handler,
        CreateSchedulingRequestCommand command,
        TestDbContext dbContext)
    {
        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.SingleAsync()).Status.Should().Be(SchedulingRequestStatus.Failed);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    private sealed class FixedRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }

    private sealed class FailingRouteDurationProvider(
        RouteDurationProviderFailureKind failureKind) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromException<RouteDurationMatrix>(new RouteDurationProviderException(
                "OpenRouteService",
                failureKind,
                "safe provider failure",
                new HttpRequestException("sensitive transport detail")));
    }

    private sealed class CallerCancelingFailingRouteDurationProvider(
        CancellationTokenSource callerCancellation) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            return Task.FromException<RouteDurationMatrix>(new RouteDurationProviderException(
                "OpenRouteService",
                RouteDurationProviderFailureKind.Timeout,
                "safe provider failure",
                new TaskCanceledException("transient timeout")));
        }
    }

    private sealed class UnexpectedFailureRouteDurationProvider(Exception exception)
        : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromException<RouteDurationMatrix>(exception);
    }

    private sealed class CallerCancelingRouteDurationProvider(CancellationTokenSource callerCancellation)
        : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            return Task.FromCanceled<RouteDurationMatrix>(cancellationToken);
        }
    }

    private sealed class FailOnceRouteDurationProvider : IRouteDurationProvider
    {
        public int CallCount { get; private set; }

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                return Task.FromException<RouteDurationMatrix>(new RouteDurationProviderException(
                    "OpenRouteService",
                    RouteDurationProviderFailureKind.Timeout,
                    "safe provider failure",
                    new TaskCanceledException("transient timeout")));
            }

            var durations = new int[points.Count, points.Count];
            for (var from = 0; from < points.Count; from++)
            {
                for (var to = 0; to < points.Count; to++)
                {
                    durations[from, to] = from == to ? 0 : 10;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }

    private sealed class FunnelMetricCapture : IDisposable
    {
        private readonly MeterListener _listener;

        public FunnelMetricCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? attempt = tags.ToArray()
                    .SingleOrDefault(tag => tag.Key == "attempt").Value?.ToString();
                string? outcome = tags.ToArray()
                    .SingleOrDefault(tag => tag.Key == "outcome").Value?.ToString();
                string? stage = tags.ToArray()
                    .SingleOrDefault(tag => tag.Key == "stage").Value?.ToString();
                if (instrument.Name == CandidatePoolFunnelTelemetry.AttemptCountName)
                {
                    Attempts.Add($"{attempt}:{outcome}");
                }

                if (instrument.Name == CandidatePoolFunnelTelemetry.StageCandidateCountName
                    && stage == CandidatePoolFunnelDimensions.StageFinalizeValidFrozenPool)
                {
                    FinalizeCounts.Add((value, attempt!));
                }
            });
            _listener.Start();
        }

        public List<string> Attempts { get; } = [];

        public List<(long Value, string Attempt)> FinalizeCounts { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed record SchedulingInvariantSnapshot(
        int SequenceNo,
        long? PoiId,
        ItineraryItemKind ItemKind,
        DateTimeOffset PlannedArrival,
        DateTimeOffset PlannedDeparture,
        int StayDurationMinutes,
        int? TravelDurationToNextMinutes,
        decimal? EstimatedCost,
        bool IsMandatory,
        string? RecommendationReason);

    private sealed class RecordingRouteDurationProvider : IRouteDurationProvider
    {
        public Action? BeforeReturn { get; init; }

        public int CallCount { get; private set; }

        public int LastPointCount { get; private set; }

        public IReadOnlyList<RoutePoint> LastPoints { get; private set; } = [];

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastPointCount = points.Count;
            LastPoints = points.ToArray();
            BeforeReturn?.Invoke();
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }

    private sealed class NoOpSchedulingRequestLock : ISchedulingRequestLock
    {
        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailOnSecondAcquireSchedulingRequestLock : ISchedulingRequestLock
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return CallCount == 1
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("reservation release failed"));
        }
    }

    private sealed class FailSecondAcquireOnceSchedulingRequestLock(Exception exception)
        : ISchedulingRequestLock
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return CallCount == 2
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }
    }

    private sealed class CancelSecondAcquireOnceSchedulingRequestLock(
        CancellationTokenSource callerCancellation) : ISchedulingRequestLock
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount != 2)
            {
                return Task.CompletedTask;
            }

            callerCancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed class RejectingGenerateRateLimiter(
        string errorCode = SchedulingErrorCodes.GenerationRateLimited,
        int retryAfterSeconds = 30) : IGenerateRateLimiter
    {
        public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
            long userId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GenerateRateLimitDecision(
                Allowed: false,
                ErrorCode: errorCode,
                RetryAfterSeconds: retryAfterSeconds));
    }

    private sealed class CountingAllowingGenerateRateLimiter : IGenerateRateLimiter
    {
        public int CallCount { get; private set; }

        public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
            long userId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new GenerateRateLimitDecision(Allowed: true));
        }
    }

    private sealed class CallerCancelingRejectingGenerateRateLimiter(
        CancellationTokenSource callerCancellation) : IGenerateRateLimiter
    {
        public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
            long userId,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            return ValueTask.FromCanceled<GenerateRateLimitDecision>(cancellationToken);
        }
    }

    private sealed class RecordingSchedulingRequestLock : ISchedulingRequestLock
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPoiRankingProvider : IPoiRankingProvider
    {
        public Action? BeforeReturn { get; init; }

        public Func<PoiRankingRequest, IReadOnlyCollection<PoiRankingItem>>? RankedItemsFactory
        {
            get;
            init;
        }

        public int CallCount { get; private set; }

        public PoiRankingRequest? LastRequest { get; private set; }

        public Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            BeforeReturn?.Invoke();
            return Task.FromResult(Result.Success(new PoiRankingResult(
                RankedItemsFactory?.Invoke(request)
                ?? request.Candidates
                    .Select(candidate => new PoiRankingItem(candidate.PoiId, 0.5m, null))
                    .ToArray())));
        }
    }

    private sealed class RecordingItineraryExplanationProvider
        : IItineraryExplanationProvider
    {
        public Action? BeforeCall { get; init; }

        public Func<ItineraryExplanationInput, Result<ItineraryExplanationResult>>?
            ResultFactory
        { get; init; }

        public Func<
            ItineraryExplanationInput,
            CancellationToken,
            Task<Result<ItineraryExplanationResult>>>? AsyncResultFactory
        { get; init; }

        public int CallCount { get; private set; }

        public ItineraryExplanationInput? LastInput { get; private set; }

        public async Task<Result<ItineraryExplanationResult>> ExplainAsync(
            ItineraryExplanationInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastInput = input;
            BeforeCall?.Invoke();
            if (AsyncResultFactory is not null)
            {
                return await AsyncResultFactory(input, cancellationToken);
            }

            return ResultFactory?.Invoke(input)
                ?? Result.Failure<ItineraryExplanationResult>(
                    ExplanationProviderErrorCodes.InvalidResponse,
                    "No test result was configured.");
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