using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetDetails;

public sealed record GetActiveTripDetailsQuery(long TripId)
    : IRequest<Result<ActiveTripDetailDto>>;

public sealed record ActiveTripDetailDto(
    string TripId,
    string TripCode,
    string TripType,
    string CurrentState,
    string GroupOrTraveler,
    string? Destination,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastSyncedAtUtc,
    int? CurrentDay,
    int Members,
    int OpenAlerts,
    GroupPanelDto[]? GroupPanel,
    CurrentLocationDto? CurrentLocation,
    ItineraryProgressItemDto[] ItineraryProgress,
    TripStateChangeDto[] StateHistory,
    TripIncidentDto[] Incidents,
    TripReroutingEventDto[] ReroutingEvents,
    TripLocationPointDto[] LocationTrail);

public sealed record GroupPanelDto(
    string GroupId,
    string GroupName,
    string HostName,
    GroupMemberDto[] Members);

public sealed record GroupMemberDto(
    string UserId,
    string FullName,
    DateTimeOffset JoinedAtUtc,
    bool IsLocationSharingEnabled);

public sealed record CurrentLocationDto(
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset? AsOfUtc);

public sealed record ItineraryProgressItemDto(
    string ItemId,
    int SequenceNo,
    string? PoiId,
    string? PoiName,
    string ItemKind,
    string Status,
    DateTimeOffset? PlannedArrivalUtc,
    DateTimeOffset? PlannedDepartureUtc,
    int StayDurationMinutes);

public sealed record TripStateChangeDto(
    string? FromState,
    string ToState,
    string? Reason,
    string? TriggeredBy,
    DateTimeOffset ChangedAtUtc);

public sealed record TripIncidentDto(
    string IncidentId,
    string IncidentType,
    string? Description,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    WeatherEventDto? WeatherEvent);

public sealed record WeatherEventDto(
    string EventType,
    string Severity,
    string? RegionName,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ValidToUtc);

public sealed record TripReroutingEventDto(
    string ReroutingId,
    string IncidentId,
    string Status,
    DateTimeOffset ProposedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    bool HasProposedItinerarySnapshot);

public sealed record TripLocationPointDto(
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset RecordedAtUtc,
    bool IsOfflineCaptured);