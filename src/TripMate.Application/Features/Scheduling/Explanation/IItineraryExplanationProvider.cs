using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Scheduling.Explanation;

public interface IItineraryExplanationProvider
{
    Task<Result<ItineraryExplanationResult>> ExplainAsync(
        ItineraryExplanationInput input,
        CancellationToken cancellationToken);
}