using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Create;

public sealed record CreateSchedulingRequestCommand(
    long TravelerUserId,
    Guid IdempotencyKey,
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
    IReadOnlyCollection<long>? MandatoryPoiIds,
    RestPreference RestPreference)
    : IRequest<Result<SchedulingResponseDto>>;