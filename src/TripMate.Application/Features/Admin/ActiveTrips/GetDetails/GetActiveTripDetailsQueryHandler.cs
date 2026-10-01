using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetDetails;

public sealed class GetActiveTripDetailsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetActiveTripDetailsQuery, Result<ActiveTripDetailDto>>
{
    private static readonly string[] ActiveStates =
        [TripSession.NavigatingState, TripSession.ExploringState, TripSession.InterruptedState];

    private const int LocationTrailLimit = 100;

    public async Task<Result<ActiveTripDetailDto>> Handle(
        GetActiveTripDetailsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<ActiveTripDetailDto>(
                ActiveTripErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var session = await dbContext.TripSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.TripId && ActiveStates.Contains(candidate.FsmState))
            .Select(candidate => new SessionRow(
                candidate.Id,
                candidate.ItineraryId,
                candidate.Itinerary.SourceType,
                candidate.Itinerary.SourceTourId,
                candidate.FsmState,
                candidate.StartedAtUtc,
                candidate.LastSyncedAtUtc,
                candidate.CurrentLatitude,
                candidate.CurrentLongitude,
                candidate.TravelerUser.FullName,
                candidate.TravelerUser.Email))
            .FirstOrDefaultAsync(cancellationToken);

        if (session is null)
        {
            // D2: unknown and non-active sessions are indistinguishable for UC-59. No audit row
            // is written because no GPS-bearing detail was accessed.
            return Result.Failure<ActiveTripDetailDto>(
                ActiveTripErrorCodes.NotFound,
                ActiveTripErrorCodes.NotFoundMessage);
        }

        // BR-130 (D8): fail-closed — the access audit row is persisted before any GPS-bearing
        // detail is assembled; an audit failure fails the request instead of serving untracked.
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId,
            AuditActionTypes.ActiveTripDetailsViewed,
            AuditEntityTypes.TripSession,
            request.TripId,
            dateTimeProvider.UtcNow,
            AuditOutcome.Success));
        await dbContext.SaveChangesAsync(cancellationToken);

        var itineraryId = session.ItineraryId;
        var groups = await dbContext.TravelGroups
            .AsNoTracking()
            .Where(group => group.ItineraryId == itineraryId)
            .OrderBy(group => group.Id)
            // The SQL column is nullable even though UC-17 creates named groups. Coalesce in
            // SQL so EF does not call GetString on a NULL value while materializing the row.
            .Select(group => new GroupRow(group.Id, group.Name ?? string.Empty, group.HostUser.FullName))
            .ToListAsync(cancellationToken);
        var groupIds = groups.Select(group => group.GroupId).ToArray();
        var members = groupIds.Length == 0
            ? []
            : await dbContext.GroupMembers
                .AsNoTracking()
                .Where(member => groupIds.Contains(member.GroupId) && member.Status == GroupMemberStatus.Active)
                .OrderBy(member => member.JoinedAtUtc)
                .ThenBy(member => member.UserId)
                .Select(member => new MemberRow(
                    member.GroupId,
                    member.UserId,
                    member.User.FullName,
                    member.JoinedAtUtc,
                    member.LocationSharingEnabled))
                .ToListAsync(cancellationToken);

        var destinationNames = session.SourceTourId.HasValue
            ? await dbContext.TourDestinations
                .AsNoTracking()
                .Where(link => link.TourId == session.SourceTourId.Value)
                .OrderBy(link => link.SequenceNo)
                .Select(link => link.Destination.Name)
                .ToListAsync(cancellationToken)
            : [];

        var itineraryProgress = await dbContext.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == itineraryId)
            .OrderBy(item => item.SequenceNo)
            .Select(item => new ProgressRow(
                item.Id,
                item.SequenceNo,
                item.PointOfInterestId,
                item.PointOfInterest != null ? item.PointOfInterest.Name : null,
                item.Kind,
                item.Status,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                item.StayDurationMinutes))
            .ToListAsync(cancellationToken);

        var stateHistory = await dbContext.TripStateHistories
            .AsNoTracking()
            .Where(history => history.SessionId == request.TripId)
            .OrderBy(history => history.ChangedAtUtc)
            .ThenBy(history => history.Id)
            .Select(history => new HistoryRow(
                history.FromState,
                history.ToState,
                history.Reason,
                history.TriggeredBy,
                history.ChangedAtUtc))
            .ToListAsync(cancellationToken);

        var incidents = await dbContext.Incidents
            .AsNoTracking()
            .Where(incident => incident.SessionId == request.TripId)
            .OrderByDescending(incident => incident.DetectedAtUtc)
            .ThenByDescending(incident => incident.Id)
            .ToListAsync(cancellationToken);
        var weatherEventIds = incidents
            .Where(incident => incident.WeatherEventId.HasValue)
            .Select(incident => incident.WeatherEventId!.Value)
            .Distinct()
            .ToArray();
        var weatherEvents = weatherEventIds.Length == 0
            ? []
            : await dbContext.WeatherEvents
                .AsNoTracking()
                .Where(weatherEvent => weatherEventIds.Contains(weatherEvent.Id))
                .ToListAsync(cancellationToken);
        var weatherById = weatherEvents.ToDictionary(weatherEvent => weatherEvent.Id);

        var reroutingEvents = await dbContext.ReroutingEvents
            .AsNoTracking()
            .Where(reroutingEvent => reroutingEvent.SessionId == request.TripId)
            .OrderByDescending(reroutingEvent => reroutingEvent.ProposedAtUtc)
            .ThenByDescending(reroutingEvent => reroutingEvent.Id)
            .Select(reroutingEvent => new ReroutingRow(
                reroutingEvent.Id,
                reroutingEvent.IncidentId,
                reroutingEvent.Status,
                reroutingEvent.ProposedAtUtc,
                reroutingEvent.DecidedAtUtc))
            .ToListAsync(cancellationToken);

        // Latest N logs selected descending, returned ascending for display (spec D4).
        var locationTrailRows = await dbContext.TripLocationLogs
            .AsNoTracking()
            .Where(log => log.SessionId == request.TripId)
            .OrderByDescending(log => log.RecordedAtUtc)
            .ThenByDescending(log => log.Id)
            .Take(LocationTrailLimit)
            .Select(log => new TrailRow(
                log.Latitude,
                log.Longitude,
                log.RecordedAtUtc,
                log.IsOfflineCaptured))
            .ToListAsync(cancellationToken);
        locationTrailRows.Reverse();

        return Result.Success(BuildDetail(
            session,
            groups,
            members,
            destinationNames,
            itineraryProgress,
            stateHistory,
            incidents,
            weatherById,
            reroutingEvents,
            locationTrailRows,
            dateTimeProvider.UtcNow));
    }

    private static ActiveTripDetailDto BuildDetail(
        SessionRow session,
        List<GroupRow> groups,
        List<MemberRow> members,
        List<string> destinationNames,
        List<ProgressRow> progressRows,
        List<HistoryRow> historyRows,
        List<Incident> incidents,
        Dictionary<long, WeatherEvent> weatherById,
        List<ReroutingRow> reroutingRows,
        List<TrailRow> trailRows,
        DateTimeOffset nowUtc)
    {
        var names = groups.Select(group => group.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToArray();
        var groupOrTraveler = names.Length > 0
            ? string.Join(", ", names)
            : !string.IsNullOrWhiteSpace(session.TravelerName)
                ? session.TravelerName.Trim()
                : session.TravelerEmail ?? "Not available";
        var memberCount = groups.Count == 0
            ? 1
            : members.Select(member => member.UserId).Distinct().Count();
        var destination = session.SourceTourId.HasValue
            ? string.Join(", ", destinationNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct())
            : null;

        var currentLocation =
            session.CurrentLatitude.HasValue && session.CurrentLongitude.HasValue
                ? new CurrentLocationDto(
                    session.CurrentLatitude.Value,
                    session.CurrentLongitude.Value,
                    session.LastSyncedAtUtc)
                : null;

        var groupPanels = groups.Count == 0
            ? null
            : groups.Select(group => new GroupPanelDto(
                group.GroupId.ToString(CultureInfo.InvariantCulture),
                !string.IsNullOrWhiteSpace(group.Name)
                    ? group.Name.Trim()
                    : $"Group {group.GroupId.ToString(CultureInfo.InvariantCulture)}",
                group.HostName,
                members
                    .Where(member => member.GroupId == group.GroupId)
                    .Select(member => new GroupMemberDto(
                        member.UserId.ToString(CultureInfo.InvariantCulture),
                        member.FullName,
                        member.JoinedAtUtc,
                        member.LocationSharingEnabled))
                    .ToArray()))
                .ToArray();

        return new ActiveTripDetailDto(
            session.SessionId.ToString(CultureInfo.InvariantCulture),
            $"TRIP-{session.SessionId.ToString(CultureInfo.InvariantCulture)}",
            session.SourceType == Itinerary.BookedTourSourceType
                ? GetActiveTripsQuery.TourType
                : GetActiveTripsQuery.SelfPlannedType,
            session.FsmState,
            groupOrTraveler,
            string.IsNullOrEmpty(destination) ? null : destination,
            session.StartedAtUtc,
            session.LastSyncedAtUtc,
            session.StartedAtUtc.HasValue
                ? VietnamCalendar.CurrentDay(session.StartedAtUtc.Value, nowUtc)
                : null,
            memberCount,
            incidents.Count(incident => incident.ResolvedAtUtc == null),
            groupPanels,
            currentLocation,
            progressRows.Select(row => new ItineraryProgressItemDto(
                row.ItemId.ToString(CultureInfo.InvariantCulture),
                row.SequenceNo,
                row.PoiId?.ToString(CultureInfo.InvariantCulture),
                row.PoiName,
                row.ItemKind.ToString(),
                row.Status,
                row.PlannedArrivalUtc,
                row.PlannedDepartureUtc,
                row.StayDurationMinutes)).ToArray(),
            historyRows.Select(row => new TripStateChangeDto(
                row.FromState,
                row.ToState,
                row.Reason,
                row.TriggeredBy,
                row.ChangedAtUtc)).ToArray(),
            incidents.Select(incident => new TripIncidentDto(
                incident.Id.ToString(CultureInfo.InvariantCulture),
                incident.IncidentType,
                incident.Description,
                incident.DetectedAtUtc,
                incident.ResolvedAtUtc,
                incident.WeatherEventId.HasValue
                    && weatherById.TryGetValue(incident.WeatherEventId.Value, out var weatherEvent)
                        ? new WeatherEventDto(
                            weatherEvent.EventType,
                            weatherEvent.Severity,
                            weatherEvent.RegionName,
                            weatherEvent.ValidFromUtc,
                            weatherEvent.ValidToUtc)
                        : null)).ToArray(),
            reroutingRows.Select(row => new TripReroutingEventDto(
                row.ReroutingId.ToString(CultureInfo.InvariantCulture),
                row.IncidentId.ToString(CultureInfo.InvariantCulture),
                row.Status,
                row.ProposedAtUtc,
                row.DecidedAtUtc,
                // proposed_itinerary_snapshot is NOT NULL, so a snapshot always exists; its
                // content is excluded by the approved contract (spec D5).
                HasProposedItinerarySnapshot: true)).ToArray(),
            trailRows.Select(row => new TripLocationPointDto(
                row.Latitude,
                row.Longitude,
                row.RecordedAtUtc,
                row.IsOfflineCaptured)).ToArray());
    }

    private sealed record SessionRow(
        long SessionId,
        long ItineraryId,
        string SourceType,
        long? SourceTourId,
        string FsmState,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? LastSyncedAtUtc,
        decimal? CurrentLatitude,
        decimal? CurrentLongitude,
        string TravelerName,
        string? TravelerEmail);

    private sealed record GroupRow(long GroupId, string? Name, string HostName);

    private sealed record MemberRow(
        long GroupId,
        long UserId,
        string FullName,
        DateTimeOffset JoinedAtUtc,
        bool LocationSharingEnabled);

    private sealed record ProgressRow(
        long ItemId,
        int SequenceNo,
        long? PoiId,
        string? PoiName,
        ItineraryItemKind ItemKind,
        string Status,
        DateTimeOffset? PlannedArrivalUtc,
        DateTimeOffset? PlannedDepartureUtc,
        int StayDurationMinutes);

    private sealed record HistoryRow(
        string? FromState,
        string ToState,
        string? Reason,
        string? TriggeredBy,
        DateTimeOffset ChangedAtUtc);

    private sealed record ReroutingRow(
        long ReroutingId,
        long IncidentId,
        string Status,
        DateTimeOffset ProposedAtUtc,
        DateTimeOffset? DecidedAtUtc);

    private sealed record TrailRow(
        decimal Latitude,
        decimal Longitude,
        DateTimeOffset RecordedAtUtc,
        bool IsOfflineCaptured);
}