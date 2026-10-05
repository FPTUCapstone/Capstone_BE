using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Common;

public static class PlanningPoiEligibility
{
    public static bool IsPlanningReady(PointOfInterest poi) =>
        poi.SourceUrl is not null
        && poi.VerifiedAtUtc.HasValue
        && poi.OpeningHours.Any(hours =>
            !hours.IsClosed && hours.OpenTime.HasValue && hours.CloseTime.HasValue);
}