using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Scheduling.Explanation;

public interface IItineraryExplanationProvider
{
    Task<Result<ItineraryExplanationResult>> ExplainAsync(
        ItineraryExplanationInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Explains with the caller's own time budget, which replaces the provider's configured per-attempt and overall
    /// timeouts for this one call (background mode, specs/UC-10-background-explanation-spec.md B2). A provider that
    /// does not override this keeps its configured timeouts.
    /// </summary>
    Task<Result<ItineraryExplanationResult>> ExplainAsync(
        ItineraryExplanationInput input,
        TimeSpan timeBudget,
        CancellationToken cancellationToken) =>
        ExplainAsync(input, cancellationToken);
}