using System.Globalization;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.Search.V2;

/// <summary>
/// Prepared independent SQL slice, deliberately unregistered and without an HTTP route.
/// It carries raw ratings internally but cannot execute keyword/rating acceptance.
/// </summary>
public sealed class TourSearchV2IndependentReader(IApplicationDbContext context, IDateTimeProvider clock)
{
    public async Task<IndependentTourSearchPage> ReadAsync(TourSearchV2Request request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Keyword) || request.MinimumRating.HasValue
            || request.Sort is TourSearchV2Request.SortRelevance or TourSearchV2Request.SortRating)
            throw new NotSupportedException("Keyword/relevance and eligible rating execution remain blocked by approval dependencies.");

        var asOfUtc = clock.UtcNow.ToUniversalTime();
        var validation = await new TourSearchV2RequestValidator(context).ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            throw new Common.Exceptions.ValidationException(validation.Errors);

        var hasDates = request.StartDate.HasValue || request.EndDate.HasValue;
        var lower = request.StartDate.HasValue ? LocalMidnightUtc(request.StartDate.Value) : DateTimeOffset.MinValue;
        var upper = request.EndDate.HasValue ? LocalMidnightUtc(request.EndDate.Value, endExclusive: true) : DateTimeOffset.MaxValue;
        var destination = TourSearchCriteria.NormalizeDestination(request.Destination);

        return await context.ExecuteInSerializableTransactionAsync(async token =>
        {
            var tours = context.Tours.AsNoTracking().Where(tour => tour.Status == TourStatus.Approved
                && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= asOfUtc
                && tour.OperatorProfile.ApprovalStatus == OperatorApprovalStatus.Approved
                && tour.OperatorProfile.User.Status == AccountStatus.Active);
            if (request.DurationDays.HasValue) tours = tours.Where(tour => tour.DurationDays == request.DurationDays);
            if (request.Category is not null) tours = tours.Where(tour => tour.Category != null
                && tour.Category.IsActive && tour.Category.Code == request.Category);
            if (request.MinPrice.HasValue) tours = tours.Where(tour => tour.BasePrice >= request.MinPrice);
            if (request.MaxPrice.HasValue) tours = tours.Where(tour => tour.BasePrice <= request.MaxPrice);
            if (destination is not null) tours = tours.Where(tour => tour.Destinations.Any(link =>
                EF.Functions.Collate(link.Destination.Name, DestinationSearchCollation).Contains(destination)));
            if (hasDates) tours = tours.Where(tour => tour.Schedules.Any(schedule =>
                schedule.Status != TourScheduleStatus.Cancelled && schedule.EndAtUtc > schedule.StartAtUtc
                && schedule.StartAtUtc >= lower && schedule.StartAtUtc < upper));

            var count = await tours.LongCountAsync(token);
            var offset = ((long)request.Page - 1) * request.PageSize;
            var pages = count / request.PageSize + (count % request.PageSize == 0 ? 0 : 1);
            if (offset >= count)
                return new IndependentTourSearchPage(request.Page, request.PageSize, count, pages, asOfUtc.UtcDateTime, []);

            var candidates = tours.SelectMany(tour => tour.Schedules.Where(schedule =>
                    schedule.Status != TourScheduleStatus.Cancelled && schedule.EndAtUtc > schedule.StartAtUtc
                    && (hasDates ? schedule.StartAtUtc >= lower && schedule.StartAtUtc < upper : schedule.StartAtUtc >= asOfUtc))
                .OrderBy(schedule => schedule.StartAtUtc).ThenBy(schedule => schedule.Id).Take(1).DefaultIfEmpty(),
                (tour, schedule) => new
                {
                    tour.Id,
                    tour.Title,
                    tour.BasePrice,
                    tour.DurationDays,
                    OperatorName = tour.OperatorProfile.CompanyName,
                    ScheduleId = schedule == null ? null : (long?)schedule.Id,
                    Departure = schedule == null ? null : (DateTimeOffset?)schedule.StartAtUtc,
                    TotalCapacity = schedule == null ? null : (int?)schedule.TotalCapacity,
                    ReservedCapacity = schedule == null ? null : (int?)schedule.ReservedCapacity,
                });
            var ratings = TripReviewAggregateReader.QueryTourRatings(context, tours);
            var ratedCandidates = from candidate in candidates
                                  join rating in ratings on candidate.Id equals rating.TourId
                                  select new { Candidate = candidate, rating.RawAverage };
            var ordered = request.Sort switch
            {
                TourSearchV2Request.SortPriceAscending => ratedCandidates.OrderBy(row => row.Candidate.BasePrice)
                    .ThenBy(row => row.Candidate.Title).ThenBy(row => row.Candidate.Id),
                TourSearchV2Request.SortPriceDescending => ratedCandidates.OrderByDescending(row => row.Candidate.BasePrice)
                    .ThenBy(row => row.Candidate.Title).ThenBy(row => row.Candidate.Id),
                _ => ratedCandidates.OrderBy(row => row.Candidate.Departure == null).ThenBy(row => row.Candidate.Departure)
                    .ThenBy(row => row.Candidate.Title).ThenBy(row => row.Candidate.Id),
            };
            // SQL Server OFFSET is int-sized in EF. The count guard above proves oversized
            // valid pages are empty for current storage; a larger database needs keyset/window paging.
            if (offset > int.MaxValue)
                throw new NotSupportedException("An in-range offset beyond SQL paging representation requires a window-query adapter.");
            var rows = await ordered.Skip((int)offset).Take(request.PageSize).ToListAsync(token);
            var ids = rows.Select(row => row.Candidate.Id).ToArray();
            var destinations = await context.TourDestinations.AsNoTracking().Where(link => ids.Contains(link.TourId))
                .OrderBy(link => link.TourId).ThenBy(link => link.SequenceNo)
                .Select(link => new { link.TourId, link.Destination.Name }).ToListAsync(token);
            var labels = destinations.GroupBy(link => link.TourId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(link => link.Name).ToArray());
            var thumbnails = await context.TourMedia.AsNoTracking().Where(media => ids.Contains(media.TourId) && media.IsPrimary)
                .ToDictionaryAsync(media => media.TourId, media => media.DeliveryUrl, token);
            var items = rows.Select(row => new IndependentTourSearchItem(
                row.Candidate.Id.ToString(CultureInfo.InvariantCulture), row.Candidate.Title,
                thumbnails.GetValueOrDefault(row.Candidate.Id), labels.GetValueOrDefault(row.Candidate.Id) ?? [],
                row.Candidate.DurationDays, checked((long)row.Candidate.BasePrice), row.Candidate.OperatorName,
                row.Candidate.ScheduleId?.ToString(CultureInfo.InvariantCulture), row.Candidate.Departure?.UtcDateTime,
                row.Candidate.TotalCapacity, row.Candidate.ReservedCapacity)
            {
                RawAverage = row.RawAverage,
            }).ToArray();
            return new IndependentTourSearchPage(request.Page, request.PageSize, count, pages, asOfUtc.UtcDateTime, items);
        }, cancellationToken);
    }

    // SQL Server Vietnamese collation preserves Vietnamese letter distinctions even
    // with AI (Huế/hue and Đà Nẵng/da nang do not match). Verified on the SQL provider.
    public const string DestinationSearchCollation = "Latin1_General_100_CI_AI";

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, bool endExclusive = false)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        // Use the local date's offset before arithmetic, including representable edge dates.
        var ticks = local.Ticks - zone.GetUtcOffset(local).Ticks + (endExclusive ? TimeSpan.TicksPerDay : 0);
        return new DateTimeOffset(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero);
    }
}

public sealed record IndependentTourSearchPage(int Page, int PageSize, long TotalCount, long TotalPages,
    DateTime AsOfUtc, IReadOnlyList<IndependentTourSearchItem> Items);

/// <summary>Independent projection carries raw rating only as internal query data.</summary>
public sealed record IndependentTourSearchItem(string TourId, string Title, string? ThumbnailUrl,
    IReadOnlyList<string> Destinations, int DurationDays, long BasePrice, string OperatorName,
    string? RepresentativeScheduleId, DateTime? DepartureAtUtc, int? TotalCapacity, int? ReservedCapacity)
{
    internal decimal? RawAverage { get; init; }

    private bool ValidCapacity => TotalCapacity is > 0 && ReservedCapacity is >= 0 && ReservedCapacity <= TotalCapacity;
    public string AvailabilityStatus => RepresentativeScheduleId is null ? "noUpcomingSchedule"
        : !ValidCapacity ? "unknown" : ReservedCapacity < TotalCapacity ? "available" : "soldOut";
    public int? RemainingSlots => ValidCapacity ? TotalCapacity - ReservedCapacity : null;

    /// <summary>Caller supplies an approved display aggregate; eligibility and midpoint rounding remain external gates.</summary>
    public TourSearchV2ItemDto ToContract(decimal? approvedDisplayAggregateRating)
    {
        if (approvedDisplayAggregateRating.HasValue
            && (approvedDisplayAggregateRating is < 1 or > 5
                || decimal.Truncate(approvedDisplayAggregateRating.Value * 10) != approvedDisplayAggregateRating.Value * 10))
            throw new ArgumentOutOfRangeException(nameof(approvedDisplayAggregateRating),
                "The approved rating provider must supply a one-decimal display value in the 1..5 scale.");

        return new TourSearchV2ItemDto(TourId, Title, ThumbnailUrl, Destinations, DurationDays, BasePrice, "VND",
            approvedDisplayAggregateRating, RemainingSlots, AvailabilityStatus, RepresentativeScheduleId, DepartureAtUtc, OperatorName);
    }
}