using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetList;

public sealed class GetActiveTripsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetActiveTripsQuery, Result<ActiveTripsResponseDto>>
{
    private static readonly string[] ActiveStates =
        [TripSession.NavigatingState, TripSession.ExploringState, TripSession.InterruptedState];

    public async Task<Result<ActiveTripsResponseDto>> Handle(
        GetActiveTripsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<ActiveTripsResponseDto>(
                ActiveTripErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var active = dbContext.TripSessions
            .AsNoTracking()
            .Where(session => ActiveStates.Contains(session.FsmState));

        var summary = await BuildSummaryAsync(active, cancellationToken);
        var filtered = ApplyFilters(active, request);
        var totalCount = await filtered.CountAsync(cancellationToken);

        var skipLong = ((long)request.PageNumber - 1) * request.PageSize;
        var pageRows = skipLong > int.MaxValue
            ? []
            : await filtered
                .OrderBy(session => session.StartedAtUtc == null)
                .ThenByDescending(session => session.StartedAtUtc)
                .ThenByDescending(session => session.Id)
                .Skip((int)skipLong)
                .Take(request.PageSize)
                .Select(session => new PageRow(
                    session.Id,
                    session.ItineraryId,
                    session.Itinerary.SourceType,
                    session.Itinerary.SourceTourId,
                    session.TravelerUser.FullName,
                    session.TravelerUser.Email,
                    session.FsmState,
                    session.StartedAtUtc))
                .ToListAsync(cancellationToken);

        var items = await EnrichPageAsync(pageRows, dateTimeProvider.UtcNow, cancellationToken);
        var totalPages = request.PageSize > 0
            ? (int)Math.Ceiling(totalCount / (double)request.PageSize)
            : 0;

        return Result.Success(new ActiveTripsResponseDto(
            summary,
            request.PageNumber,
            request.PageSize,
            totalCount,
            totalPages,
            items));
    }

    private IQueryable<TripSession> ApplyFilters(
        IQueryable<TripSession> query,
        GetActiveTripsQuery request)
    {
        if (!string.IsNullOrWhiteSpace(request.TripType))
        {
            query = request.TripType == GetActiveTripsQuery.TourType
                ? query.Where(session => session.Itinerary.SourceType == Itinerary.BookedTourSourceType)
                : query.Where(session => session.Itinerary.SourceType != Itinerary.BookedTourSourceType);
        }

        if (!string.IsNullOrWhiteSpace(request.Destination))
        {
            var pattern = ContainsPattern(request.Destination.Trim());
            query = query.Where(session =>
                session.Itinerary.SourceTourId != null &&
                dbContext.TourDestinations.Any(link =>
                    link.TourId == session.Itinerary.SourceTourId &&
                    EF.Functions.Like(link.Destination.Name, pattern, "\\")));
        }

        if (request.StartDateFrom.HasValue)
        {
            var fromUtc = VietnamCalendar.StartUtc(request.StartDateFrom.Value);
            query = query.Where(session => session.StartedAtUtc >= fromUtc);
        }

        if (request.StartDateTo.HasValue)
        {
            var untilUtc = VietnamCalendar.StartUtc(request.StartDateTo.Value.AddDays(1));
            query = query.Where(session => session.StartedAtUtc < untilUtc);
        }

        if (!string.IsNullOrWhiteSpace(request.AlertState))
        {
            var withAlerts = request.AlertState == GetActiveTripsQuery.WithOpenAlerts;
            query = query.Where(session => dbContext.Incidents.Any(incident =>
                incident.SessionId == session.Id &&
                incident.ResolvedAtUtc == null) == withAlerts);
        }

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var pattern = ContainsPattern(request.Keyword.Trim());
            query = query.Where(session =>
                EF.Functions.Like("TRIP-" + session.Id.ToString(), pattern, "\\") ||
                dbContext.TravelGroups.Any(group =>
                    group.ItineraryId == session.ItineraryId &&
                    EF.Functions.Like(group.Name, pattern, "\\")) ||
                (session.Itinerary.SourceTourId != null &&
                 dbContext.Tours.Any(tour =>
                     tour.Id == session.Itinerary.SourceTourId &&
                     EF.Functions.Like(tour.Title, pattern, "\\"))));
        }

        return query;
    }

    private async Task<ActiveTripSummaryDto> BuildSummaryAsync(
        IQueryable<TripSession> active,
        CancellationToken cancellationToken)
    {
        var activeTrips = await active.CountAsync(cancellationToken);
        var tripsWithOpenAlerts = await active
            .CountAsync(session => dbContext.Incidents.Any(incident =>
                incident.SessionId == session.Id && incident.ResolvedAtUtc == null), cancellationToken);

        var activeItineraryIds = active.Select(session => session.ItineraryId);
        var sessionTravelers = active.Select(session => session.TravelerUserId);
        var groupTravelers = dbContext.GroupMembers
            .AsNoTracking()
            .Where(member =>
                member.Status == GroupMemberStatus.Active &&
                activeItineraryIds.Contains(member.TravelGroup.ItineraryId))
            .Select(member => member.UserId);
        var travelersOnTrip = await sessionTravelers.Union(groupTravelers).Distinct().CountAsync(cancellationToken);

        return new ActiveTripSummaryDto(activeTrips, tripsWithOpenAlerts, travelersOnTrip);
    }

    private async Task<IReadOnlyList<ActiveTripListItemDto>> EnrichPageAsync(
        IReadOnlyList<PageRow> rows,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var sessionIds = rows.Select(row => row.SessionId).ToArray();
        var itineraryIds = rows.Select(row => row.ItineraryId).Distinct().ToArray();
        var tourIds = rows.Where(row => row.SourceTourId.HasValue)
            .Select(row => row.SourceTourId!.Value).Distinct().ToArray();

        var groups = await dbContext.TravelGroups.AsNoTracking()
            .Where(group => itineraryIds.Contains(group.ItineraryId))
            .OrderBy(group => group.Id)
            .Select(group => new GroupRow(group.Id, group.ItineraryId, group.Name))
            .ToListAsync(cancellationToken);
        var groupIds = groups.Select(group => group.GroupId).ToArray();
        var activeMembers = await dbContext.GroupMembers.AsNoTracking()
            .Where(member => groupIds.Contains(member.GroupId) && member.Status == GroupMemberStatus.Active)
            .Select(member => new MemberRow(member.GroupId, member.UserId))
            .ToListAsync(cancellationToken);
        var openAlertCounts = await dbContext.Incidents.AsNoTracking()
            .Where(incident => sessionIds.Contains(incident.SessionId) && incident.ResolvedAtUtc == null)
            .GroupBy(incident => incident.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.SessionId, item => item.Count, cancellationToken);
        var destinationRows = tourIds.Length == 0
            ? []
            : await dbContext.TourDestinations.AsNoTracking()
                .Where(link => tourIds.Contains(link.TourId))
                .OrderBy(link => link.SequenceNo)
                .Select(link => new DestinationRow(link.TourId, link.Destination.Name, link.SequenceNo))
                .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var itineraryGroups = groups.Where(group => group.ItineraryId == row.ItineraryId).ToList();
            var names = itineraryGroups.Select(group => group.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
            var traveler = !string.IsNullOrWhiteSpace(row.TravelerName)
                ? row.TravelerName.Trim()
                : row.TravelerEmail ?? "Not available";
            var members = itineraryGroups.Count == 0
                ? 1
                : activeMembers
                    .Where(member => itineraryGroups.Any(group => group.GroupId == member.GroupId))
                    .Select(member => member.UserId)
                    .Distinct()
                    .Count();
            var destination = row.SourceTourId.HasValue
                ? string.Join(", ", destinationRows
                    .Where(item => item.TourId == row.SourceTourId.Value)
                    .OrderBy(item => item.SequenceNo)
                    .Select(item => item.Name)
                    .Distinct())
                : null;

            return new ActiveTripListItemDto(
                row.SessionId.ToString(CultureInfo.InvariantCulture),
                $"TRIP-{row.SessionId.ToString(CultureInfo.InvariantCulture)}",
                row.SourceType == Itinerary.BookedTourSourceType
                    ? GetActiveTripsQuery.TourType
                    : GetActiveTripsQuery.SelfPlannedType,
                row.FsmState,
                names.Length > 0 ? string.Join(", ", names) : traveler,
                string.IsNullOrEmpty(destination) ? null : destination,
                row.StartedAtUtc,
                row.StartedAtUtc.HasValue ? VietnamCalendar.CurrentDay(row.StartedAtUtc.Value, nowUtc) : null,
                members,
                openAlertCounts.GetValueOrDefault(row.SessionId));
        }).ToList();
    }

    private static string ContainsPattern(string value) =>
        $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)}%";

    private sealed record PageRow(
        long SessionId,
        long ItineraryId,
        string SourceType,
        long? SourceTourId,
        string TravelerName,
        string? TravelerEmail,
        string FsmState,
        DateTimeOffset? StartedAtUtc);

    private sealed record GroupRow(long GroupId, long ItineraryId, string? Name);
    private sealed record MemberRow(long GroupId, long UserId);
    private sealed record DestinationRow(long TourId, string Name, int SequenceNo);
}