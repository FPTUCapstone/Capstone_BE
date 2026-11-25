using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Engine.Strategies;

/// <summary>
/// Spec 4.3 <c>ScheduleDelay</c> on the next Pending stop: <c>ReduceStops</c>. If the current order still fits the day
/// there is nothing to fix; otherwise drop the least preferred non-mandatory stop (then pairs) until it fits.
/// </summary>
internal sealed class ScheduleDelayStrategy : IReroutingStrategy
{
    public string IncidentType => Incident.ScheduleDelayType;

    public Task<IReadOnlyList<IReadOnlyList<ReroutingVariant>>> CreateBatchesAsync(
        ReroutingStrategyContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ReroutingVariants.ReduceStopsBatches(context));
}