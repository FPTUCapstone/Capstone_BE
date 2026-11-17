using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Explanation;

/// <summary>
/// One background explanation (specs/UC-10-background-explanation-spec.md, B2-B5): ask the AI with the background
/// time budget, validate, and overwrite only the items that still hold the template sentence saved at creation.
/// </summary>
public sealed class BackgroundItineraryExplanationProcessorTests
{
    private const long TravelerId = 7;
    private const long MuseumPoiId = 12;
    private const long MarketPoiId = 28;
    private const string MuseumTemplate = "Bảo tàng là điểm đến bắt buộc theo yêu cầu của bạn.";
    private const string RestTemplate = "Thời gian nghỉ ngơi tự do giữa các điểm đến.";
    private const string MarketTemplate = "Chợ là điểm gợi ý phù hợp với sở thích và khung giờ của bạn.";
    private const string MuseumAi = "Bạn bắt đầu ngày mới ở bảo tàng, điểm bạn đã chọn.";
    private const string RestAi = "Bạn có 30 phút nghỉ để lấy lại sức.";
    private const string MarketAi = "Bạn ghé chợ để thưởng thức ẩm thực địa phương.";

    private static readonly DateTimeOffset Start = new(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessAsync_AValidAnswer_ReplacesEveryTemplateSentence()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };
        var logger = new RecordingLogger();

        BackgroundItineraryExplanationResult result = await Processor(db, provider, logger: logger)
            .ProcessAsync(job, CancellationToken.None);

        result.Outcome.Should().Be("success");
        result.UpdatedItems.Should().Be(3);
        (await StoredTextsAsync(db)).Should().Equal(MuseumAi, RestAi, MarketAi);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Information);
        log.Properties["Outcome"].Should().Be("success");
        log.Properties["UpdatedItems"].Should().Be(3);
        log.Properties["ItineraryId"].Should().Be(job.ItineraryId);
    }

    [Fact]
    public async Task ProcessAsync_GivesTheProviderTheBackgroundBudget_NotTheInlineOne()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };
        var options = new ItineraryExplanationExecutionOptions
        {
            Enabled = true,
            BackgroundEnabled = true,
            OverallTimeout = TimeSpan.FromSeconds(20),
            BackgroundTimeout = TimeSpan.FromSeconds(77),
        };

        await Processor(db, provider, options).ProcessAsync(job, CancellationToken.None);

        provider.Budgets.Should().Equal(TimeSpan.FromSeconds(77));
        provider.Inputs.Should().ContainSingle().Which.Should().BeSameAs(job.Input);
    }

    [Theory]
    [InlineData(ExplanationProviderErrorCodes.Network, "network")]
    [InlineData(ExplanationProviderErrorCodes.Quota, "quota")]
    [InlineData(ExplanationProviderErrorCodes.ServerError, "server-error")]
    [InlineData(ExplanationProviderErrorCodes.Timeout, "timeout")]
    [InlineData(ExplanationProviderErrorCodes.InvalidResponse, "invalid-response")]
    public async Task ProcessAsync_WhenTheProviderFails_KeepsTheTemplatesAndLogsTheOutcome(
        string errorCode,
        string expectedOutcome)
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var provider = new FakeProvider
        {
            Result = _ => Result.Failure<ItineraryExplanationResult>(errorCode, "provider failed"),
        };
        var logger = new RecordingLogger();

        BackgroundItineraryExplanationResult result = await Processor(db, provider, logger: logger)
            .ProcessAsync(job, CancellationToken.None);

        result.Outcome.Should().Be(expectedOutcome);
        result.UpdatedItems.Should().Be(0);
        (await StoredTextsAsync(db)).Should().Equal(MuseumTemplate, RestTemplate, MarketTemplate);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Outcome"].Should().Be(expectedOutcome);
        log.Properties["TimedOut"].Should().Be(errorCode == ExplanationProviderErrorCodes.Timeout);
    }

    [Fact]
    public async Task ProcessAsync_AnAnswerTheValidatorRejects_KeepsEveryTemplate()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        // The sentence for the market is attached to another place: the whole answer is refused.
        var provider = new FakeProvider
        {
            Result = _ => Result.Success(new ItineraryExplanationResult(
            [
                new ItineraryExplanationItemResult(1, MuseumPoiId, MuseumAi),
                new ItineraryExplanationItemResult(2, null, RestAi),
                new ItineraryExplanationItemResult(3, 999, MarketAi),
            ])),
        };

        BackgroundItineraryExplanationResult result = await Processor(db, provider)
            .ProcessAsync(job, CancellationToken.None);

        result.Outcome.Should().Be("invalid-response");
        (await StoredTextsAsync(db)).Should().Equal(MuseumTemplate, RestTemplate, MarketTemplate);
    }

    [Fact]
    public async Task ProcessAsync_AnItemChangedMeanwhile_IsKeptWhileTheOthersAreReplaced()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        const string changed = "Câu này đã được sửa sau khi tạo lịch.";
        ItineraryItem market = await db.ItineraryItems.SingleAsync(item => item.SequenceNo == 3);
        market.AttachFriendlyExplanation(changed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };

        BackgroundItineraryExplanationResult result = await Processor(db, provider)
            .ProcessAsync(job, CancellationToken.None);

        result.Outcome.Should().Be("success");
        result.UpdatedItems.Should().Be(2);
        (await StoredTextsAsync(db)).Should().Equal(MuseumAi, RestAi, changed);
    }

    [Fact]
    public async Task ProcessAsync_AnItemThatIsGone_IsSkippedWithoutFailing()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        db.ItineraryItems.Remove(await db.ItineraryItems.SingleAsync(item => item.SequenceNo == 2));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };

        BackgroundItineraryExplanationResult result = await Processor(db, provider)
            .ProcessAsync(job, CancellationToken.None);

        result.UpdatedItems.Should().Be(2);
        (await StoredTextsAsync(db)).Should().Equal(MuseumAi, MarketAi);
    }

    [Fact]
    public async Task ProcessAsync_ADeletedItinerary_ChangesNothingAndDoesNotThrow()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        db.ItineraryItems.RemoveRange(await db.ItineraryItems.ToListAsync());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };
        var logger = new RecordingLogger();

        BackgroundItineraryExplanationResult result = await Processor(db, provider, logger: logger)
            .ProcessAsync(job, CancellationToken.None);

        result.Outcome.Should().Be("stale");
        result.UpdatedItems.Should().Be(0);
        logger.Entries.Should().ContainSingle().Which.Properties["Outcome"].Should().Be("stale");
    }

    [Fact]
    public async Task ProcessAsync_ItemsOfAnotherItinerary_AreNeverTouched()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        ItineraryExplanationJob other = await SeedJobAsync(db);
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };

        await Processor(db, provider).ProcessAsync(job, CancellationToken.None);

        (await StoredTextsAsync(db, other.ItineraryId))
            .Should().Equal(MuseumTemplate, RestTemplate, MarketTemplate);
    }

    [Fact]
    public async Task ProcessAsync_WhenTheSaveFails_KeepsTheTemplatesAndLogsIt()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };
        var logger = new RecordingLogger();
        db.SaveChangesFailureFactory = () => new DbUpdateException("The database is unavailable.");

        BackgroundItineraryExplanationResult result = await Processor(db, provider, logger: logger)
            .ProcessAsync(job, CancellationToken.None);

        db.SaveChangesFailureFactory = null;
        result.Outcome.Should().Be("persistence-failed");
        result.UpdatedItems.Should().Be(0);
        db.ChangeTracker.Entries().Should().BeEmpty("a failed save must not leave changes for a later save");
        (await StoredTextsAsync(db)).Should().Equal(MuseumTemplate, RestTemplate, MarketTemplate);
        LogEntry log = logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Properties["Outcome"].Should().Be("persistence-failed");
    }

    [Fact]
    public async Task ProcessAsync_WhenTheBudgetRunsOut_ReportsATimeoutAndKeepsTheTemplates()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var provider = new FakeProvider
        {
            AsyncResult = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable after cancellation.");
            },
        };
        var options = new ItineraryExplanationExecutionOptions
        {
            Enabled = true,
            BackgroundEnabled = true,
            BackgroundTimeout = TimeSpan.FromMilliseconds(150),
        };
        var logger = new RecordingLogger();

        BackgroundItineraryExplanationResult result = await Processor(db, provider, options, logger)
            .ProcessAsync(job, CancellationToken.None);

        result.Outcome.Should().Be("timeout");
        (await StoredTextsAsync(db)).Should().Equal(MuseumTemplate, RestTemplate, MarketTemplate);
        logger.Entries.Should().ContainSingle().Which.Properties["TimedOut"].Should().Be(true);
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHostStops_PropagatesTheCancellation()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider
        {
            AsyncResult = async (_, token) =>
            {
                called.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable after cancellation.");
            },
        };
        using var stopping = new CancellationTokenSource();

        Task<BackgroundItineraryExplanationResult> processing = Processor(db, provider)
            .ProcessAsync(job, stopping.Token);
        await called.Task;
        await stopping.CancelAsync();

        Func<Task> act = () => processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        (await StoredTextsAsync(db)).Should().Equal(MuseumTemplate, RestTemplate, MarketTemplate);
    }

    [Fact]
    public async Task ProcessAsync_LogsNeitherPlaceNamesNorSentences()
    {
        await using var db = TestDbContext.Create();
        ItineraryExplanationJob job = await SeedJobAsync(db);
        var provider = new FakeProvider { Result = _ => Result.Success(AiAnswer()) };
        var logger = new RecordingLogger();

        await Processor(db, provider, logger: logger).ProcessAsync(job, CancellationToken.None);

        string logged = string.Join('\n', logger.Entries.Select(entry => entry.Message));
        logged.Should().NotContain("Bảo tàng").And.NotContain(MuseumAi).And.NotContain(MuseumTemplate);
    }

    private static BackgroundItineraryExplanationProcessor Processor(
        TestDbContext db,
        IItineraryExplanationProvider provider,
        ItineraryExplanationExecutionOptions? options = null,
        RecordingLogger? logger = null) =>
        new(
            db,
            provider,
            options ?? new ItineraryExplanationExecutionOptions { Enabled = true, BackgroundEnabled = true },
            logger);

    private static ItineraryExplanationResult AiAnswer() => new(
    [
        new ItineraryExplanationItemResult(1, MuseumPoiId, MuseumAi),
        new ItineraryExplanationItemResult(2, null, RestAi),
        new ItineraryExplanationItemResult(3, MarketPoiId, MarketAi),
    ]);

    /// <summary>An itinerary saved with its template sentences, and the job the create request queued for it.</summary>
    private static async Task<ItineraryExplanationJob> SeedJobAsync(TestDbContext db)
    {
        var itinerary = Itinerary.CreateManual(TravelerId, "Đà Nẵng", Itinerary.ActiveStatus, Start);
        ItineraryItem museum = ItineraryItem.CreateVisit(
            1, MuseumPoiId, Start.AddMinutes(30), Start.AddMinutes(90), true, 60_000m, "Mandatory location");
        ItineraryItem rest = ItineraryItem.CreateRest(
            2, Start.AddMinutes(105), Start.AddMinutes(135), "Nghỉ tự do");
        ItineraryItem market = ItineraryItem.CreateVisit(
            3, MarketPoiId, Start.AddMinutes(145), Start.AddMinutes(205), false, 0m, "Suggested nearby location");
        museum.AttachFriendlyExplanation(MuseumTemplate);
        rest.AttachFriendlyExplanation(RestTemplate);
        market.AttachFriendlyExplanation(MarketTemplate);
        itinerary.AddItem(museum);
        itinerary.AddItem(rest);
        itinerary.AddItem(market);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var input = new ItineraryExplanationInput(
            [
                new ItineraryExplanationItem(
                    1, MuseumPoiId, "Bảo tàng Điêu khắc Chăm", "Bảo tàng", ["văn hóa"], ItineraryItemKind.Visit, true,
                    Start.AddMinutes(30), Start.AddMinutes(90), 60, 60_000m, 15, "Mandatory location"),
                new ItineraryExplanationItem(
                    2, null, null, null, [], ItineraryItemKind.Rest, false,
                    Start.AddMinutes(105), Start.AddMinutes(135), 30, null, 10, "Free/rest time"),
                new ItineraryExplanationItem(
                    3, MarketPoiId, "Chợ Hàn", "Chợ", ["ẩm thực"], ItineraryItemKind.Visit, false,
                    Start.AddMinutes(145), Start.AddMinutes(205), 60, 0m, null, "Suggested nearby location"),
            ],
            new ItineraryExplanationContext(["am thuc", "van hoa"], Start, "Asia/Ho_Chi_Minh", 205));
        return new ItineraryExplanationJob(
            itinerary.Id,
            input,
            [
                new ItineraryExplanationJobItem(museum.Id, 1, MuseumTemplate),
                new ItineraryExplanationJobItem(rest.Id, 2, RestTemplate),
                new ItineraryExplanationJobItem(market.Id, 3, MarketTemplate),
            ]);
    }

    private static async Task<List<string?>> StoredTextsAsync(TestDbContext db, long? itineraryId = null)
    {
        long id = itineraryId ?? await db.Itineraries.AsNoTracking().Select(item => item.Id).MinAsync();
        return await db.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == id)
            .OrderBy(item => item.SequenceNo)
            .Select(item => item.FriendlyExplanation)
            .ToListAsync();
    }

    private sealed class FakeProvider : IItineraryExplanationProvider
    {
        public Func<ItineraryExplanationInput, Result<ItineraryExplanationResult>>? Result { get; init; }

        public Func<ItineraryExplanationInput, CancellationToken, Task<Result<ItineraryExplanationResult>>>?
            AsyncResult
        { get; init; }

        public List<TimeSpan?> Budgets { get; } = [];

        public List<ItineraryExplanationInput> Inputs { get; } = [];

        public Task<Result<ItineraryExplanationResult>> ExplainAsync(
            ItineraryExplanationInput input,
            CancellationToken cancellationToken) =>
            AnswerAsync(input, timeBudget: null, cancellationToken);

        public Task<Result<ItineraryExplanationResult>> ExplainAsync(
            ItineraryExplanationInput input,
            TimeSpan timeBudget,
            CancellationToken cancellationToken) =>
            AnswerAsync(input, timeBudget, cancellationToken);

        private async Task<Result<ItineraryExplanationResult>> AnswerAsync(
            ItineraryExplanationInput input,
            TimeSpan? timeBudget,
            CancellationToken cancellationToken)
        {
            Budgets.Add(timeBudget);
            Inputs.Add(input);
            if (AsyncResult is not null)
            {
                return await AsyncResult(input, cancellationToken);
            }

            return Result!(input);
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<BackgroundItineraryExplanationProcessor>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

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