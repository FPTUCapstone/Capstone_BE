using Microsoft.Extensions.Logging;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Infrastructure.Reviews.Moderation;

public sealed class LocalReviewContentModerator : IReviewContentModerator
{
    internal static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<
        ReviewText,
        CancellationToken,
        ValueTask<ReviewContentModerationResult>> _evaluateAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _watchdogDelayAsync;
    private readonly ILogger<LocalReviewContentModerator> _logger;

    public LocalReviewContentModerator(ILogger<LocalReviewContentModerator> logger)
        : this(
            DeterministicLocalReviewPolicyEvaluator.EvaluateAsync,
            static (timeout, cancellationToken) => Task.Delay(timeout, cancellationToken),
            logger)
    {
    }

    public string ActivePolicyVersion => ReviewContentPolicy.ActiveVersion;

    internal LocalReviewContentModerator(
        Func<ReviewText, CancellationToken, ValueTask<ReviewContentModerationResult>> evaluateAsync,
        Func<TimeSpan, CancellationToken, Task> watchdogDelayAsync,
        ILogger<LocalReviewContentModerator> logger)
    {
        _evaluateAsync = evaluateAsync ?? throw new ArgumentNullException(nameof(evaluateAsync));
        _watchdogDelayAsync = watchdogDelayAsync
            ?? throw new ArgumentNullException(nameof(watchdogDelayAsync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReviewContentModerationResult> ScreenAsync(
        ReviewText text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var evaluationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var watchdogCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task watchdogTask = _watchdogDelayAsync(
                ExecutionTimeout,
                watchdogCancellation.Token);
            Task<ReviewContentModerationResult> evaluationTask = Task.Run(
                async () => await _evaluateAsync(text, evaluationCancellation.Token),
                CancellationToken.None);
            Task completedTask = await Task.WhenAny(evaluationTask, watchdogTask);

            if (completedTask == watchdogTask)
            {
                await watchdogTask;
                await evaluationCancellation.CancelAsync();
                LogUnavailable("watchdog-timeout");
                return ReviewContentModerationResult.Unavailable();
            }

            await watchdogCancellation.CancelAsync();
            ReviewContentModerationResult result = await evaluationTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidResult(result))
            {
                LogUnavailable("invalid-internal-result");
                return ReviewContentModerationResult.Unavailable();
            }

            LogDecision(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnavailable(exception.GetType().Name);
            return ReviewContentModerationResult.Unavailable();
        }
    }

    private static bool IsValidResult(ReviewContentModerationResult? result) =>
        result switch
        {
            {
                Decision: ReviewModerationDecision.Accepted,
                PolicyVersion: ReviewContentPolicy.ActiveVersion,
                Categories.Count: 0,
            } => true,
            {
                Decision: ReviewModerationDecision.Rejected,
                PolicyVersion: null,
                Categories.Count: > 0,
            } => result.Categories.All(Enum.IsDefined),
            {
                Decision: ReviewModerationDecision.Unavailable,
                PolicyVersion: null,
                Categories.Count: 0,
            } => true,
            _ => false,
        };

    private void LogDecision(ReviewContentModerationResult result)
    {
        string categoryIds = result.Categories.Count == 0
            ? "none"
            : string.Join(',', result.Categories);
        _logger.LogInformation(
            "Trip review text screening completed. PolicyVersion={PolicyVersion}; Decision={Decision}; Categories={Categories}.",
            ReviewContentPolicy.ActiveVersion,
            result.Decision,
            categoryIds);
    }

    private void LogUnavailable(string failureClass) =>
        _logger.LogWarning(
            "Trip review text screening failed closed. PolicyVersion={PolicyVersion}; Decision={Decision}; FailureClass={FailureClass}.",
            ReviewContentPolicy.ActiveVersion,
            ReviewModerationDecision.Unavailable,
            failureClass);
}