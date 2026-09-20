using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed record CreateSchedulingRequest(
    DateTimeOffset StartAt,
    string TimeZoneId,
    decimal StartLatitude,
    decimal StartLongitude,
    decimal ExplorationLatitude,
    decimal ExplorationLongitude,
    long? EndPoiId,
    bool ReturnToStart,
    int AvailableMinutes,
    TransportMode TransportMode,
    decimal SearchRadiusKm,
    decimal? BudgetVnd,
    IReadOnlyCollection<long> MandatoryPoiIds,
    RestPreference RestPreference);