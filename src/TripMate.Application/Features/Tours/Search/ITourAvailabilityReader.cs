using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.Search;

public interface ITourAvailabilityReader
{
    Task<IReadOnlyDictionary<long, TourAvailabilityReadResult>> ReadAsync(
        IReadOnlyCollection<long> scheduleIds,
        CancellationToken cancellationToken);
}

public readonly record struct TourAvailabilityReadResult(
    bool IsResolved,
    int TotalCapacity,
    int ReservedCapacity)
{
    public static TourAvailabilityReadResult Failed => new(false, 0, 0);

    public static TourAvailabilityReadResult Resolved(
        int totalCapacity,
        int reservedCapacity) => new(true, totalCapacity, reservedCapacity);
}

public sealed class SqlTourAvailabilityReader(IApplicationDbContext dbContext)
    : ITourAvailabilityReader
{
    public const string QueryTag = "TM70_UC24_AVAILABILITY";

    public async Task<IReadOnlyDictionary<long, TourAvailabilityReadResult>> ReadAsync(
        IReadOnlyCollection<long> scheduleIds,
        CancellationToken cancellationToken)
    {
        var requestedIds = scheduleIds.Distinct().Order().ToArray();
        if (requestedIds.Length == 0)
        {
            return new Dictionary<long, TourAvailabilityReadResult>();
        }

        // Provider failures deliberately propagate. The handler may degrade one missing/invalid
        // row, but a failed batch/connection is UC-24 case 7.a1, not a collection of per-tour 6.a1s.
        var rows = await dbContext.TourSchedules
            .AsNoTracking()
            .TagWith(QueryTag)
            .Where(schedule => requestedIds.Contains(schedule.Id)
                && schedule.Status == TourScheduleStatus.Scheduled)
            .Select(schedule => new
            {
                schedule.Id,
                schedule.TotalCapacity,
                schedule.ReservedCapacity,
            })
            .ToArrayAsync(cancellationToken);

        var rowsById = rows.ToDictionary(row => row.Id);
        return requestedIds.ToDictionary(
            id => id,
            id => rowsById.TryGetValue(id, out var row)
                && row.TotalCapacity > 0
                && row.ReservedCapacity >= 0
                && row.ReservedCapacity <= row.TotalCapacity
                    ? TourAvailabilityReadResult.Resolved(
                        row.TotalCapacity,
                        row.ReservedCapacity)
                    : TourAvailabilityReadResult.Failed);
    }
}