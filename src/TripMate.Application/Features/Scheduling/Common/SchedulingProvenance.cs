using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>
/// How a generated schedule was produced (ADR-002). It travels with the plan, is saved on the itinerary and is returned to
/// clients, so a heuristic fallback or a node-limited CSP result is never presented as a proven CSP optimum.
/// </summary>
/// <param name="SolverOutcome">A <see cref="SchedulingSolverOutcomes"/> value, or <see cref="ManualOrder"/>.</param>
/// <param name="ObjectiveMode">The CSP objective mode; null when the CSP did not run.</param>
/// <param name="SearchCompleted">True when the CSP explored its whole search tree; null when the CSP did not run.</param>
/// <param name="TimeOfDayRefined">True when the time-of-day step reordered the solver's schedule.</param>
public sealed record SchedulingProvenance(
    string SolverOutcome,
    string? ObjectiveMode,
    bool? SearchCompleted,
    bool TimeOfDayRefined = false)
{
    /// <summary>A version saved from the traveler's own visit order: no solver chose it.</summary>
    public const string ManualOrder = "manual_order";

    public static SchedulingProvenance ForManualOrder() => new(ManualOrder, null, null);

    public string? SolverKind => SchedulingSolverKinds.Of(SolverOutcome);

    public void ApplyTo(Itinerary itinerary)
    {
        ArgumentNullException.ThrowIfNull(itinerary);
        itinerary.RecordGenerationProvenance(SolverOutcome, ObjectiveMode, SearchCompleted, TimeOfDayRefined);
    }
}

/// <summary>
/// Client view of <see cref="SchedulingProvenance"/>. <c>SolverKind</c> is <c>Csp</c>, <c>CspFallback</c>,
/// <c>Heuristic</c> or <c>MiniRouting</c>, and null for a traveler's own order.
/// </summary>
public sealed record SchedulingProvenanceDto(
    string SolverOutcome,
    string? SolverKind,
    string? ObjectiveMode,
    bool? SearchCompleted,
    bool TimeOfDayRefined)
{
    public static SchedulingProvenanceDto? From(SchedulingProvenance? provenance) =>
        provenance is null
            ? null
            : new(
                provenance.SolverOutcome,
                provenance.SolverKind,
                provenance.ObjectiveMode,
                provenance.SearchCompleted,
                provenance.TimeOfDayRefined);

    /// <summary>Null for itineraries saved before provenance was recorded.</summary>
    public static SchedulingProvenanceDto? From(Itinerary itinerary)
    {
        ArgumentNullException.ThrowIfNull(itinerary);
        return itinerary.SolverOutcome is null
            ? null
            : new(
                itinerary.SolverOutcome,
                SchedulingSolverKinds.Of(itinerary.SolverOutcome),
                itinerary.ObjectiveMode,
                itinerary.SearchCompleted,
                itinerary.TimeOfDayRefined ?? false);
    }
}