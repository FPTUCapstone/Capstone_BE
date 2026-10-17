using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// The result of scheduling one ordered list of stops without saving it: either a plan, or the stop that
/// breaks it (<see cref="Infeasibility"/>), or a general failure message.
/// </summary>
public sealed record StopsEvaluation(
    GeneratedItineraryPlan? Plan,
    StopInfeasibility? Infeasibility,
    string? FailureMessage)
{
    public bool Fits => Plan is not null;

    public static StopsEvaluation Success(GeneratedItineraryPlan plan) => new(plan, null, null);

    public static StopsEvaluation Breaks(StopInfeasibility infeasibility) => new(null, infeasibility, null);

    public static StopsEvaluation Failed(string message) => new(null, null, message);
}