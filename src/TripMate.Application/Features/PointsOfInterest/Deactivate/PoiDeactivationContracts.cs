using System.Text.Json.Serialization;

using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

public sealed record GetPoiDeactivationReferencesQuery(long PoiId) : IRequest<Result<PoiDeactivationReferencesDto>>;

public sealed record PoiDeactivationReferencesDto(
    long PoiId,
    string PoiName,
    string Status,
    int ActiveItinerariesCount,
    int ActiveRoutesCount,
    int OpenCommercialBookingsCount,
    bool HasActiveReferences,
    string ReferenceSnapshotToken,
    string? MessageCode = null,
    string? Message = null);

public sealed record DeactivatePoiCommand : IRequest<Result<DeactivatePoiResultDto>>
{
    public long PoiId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ReferenceSnapshotToken { get; init; } = string.Empty;
    [JsonIgnore] public Guid IdempotencyKey { get; init; }
}

public sealed record DeactivatePoiResultDto(
    long PoiId,
    string Status,
    int DeactivatedConnectingRoutesCount,
    DateTimeOffset DeactivatedAtUtc,
    string MessageCode,
    string Message);