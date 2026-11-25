using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// A version created by accepting a UC-15 rerouting proposal. It lives here, not in
/// <c>SchedulingProvenance</c>, because <c>Scheduling/Common</c> is frozen for this task (solver report rule).
/// <c>planning.Itineraries.solver_outcome</c> is a free <c>VARCHAR(64)</c>, so no migration is needed.
/// </summary>
public static class RerouteProvenance
{
    public const string SolverOutcome = "reroute";

    public static void ApplyTo(Itinerary itinerary)
    {
        ArgumentNullException.ThrowIfNull(itinerary);
        itinerary.RecordGenerationProvenance(SolverOutcome, null, null, false);
    }
}