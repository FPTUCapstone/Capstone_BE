using System.Globalization;
using System.Linq.Expressions;
using System.Text;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.Search;

public sealed class SearchToursQueryHandler(
    IApplicationDbContext dbContext,
    ITourAvailabilityReader availabilityReader,
    IDateTimeProvider dateTimeProvider,
    ILogger<SearchToursQueryHandler> logger)
    : IRequestHandler<SearchToursQuery, Result<PagedToursResponseDto>>
{
    private const string Currency = "VND";
    private const string SearchCollation = "Latin1_General_100_CI_AI";

    public async Task<Result<PagedToursResponseDto>> Handle(
        SearchToursQuery request,
        CancellationToken cancellationToken)
    {
        var destination = TourSearchCriteria.NormalizeDestination(request.Destination);
        var keyword = Normalize(request.Keyword);
        var tokens = keyword?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var resolvedSort = request.Sort
            ?? (keyword is null ? SearchToursQuery.SortDepartureDate : SearchToursQuery.SortRelevance);
        var now = dateTimeProvider.UtcNow.ToUniversalTime();
        var hasDates = request.DepartureDate.HasValue
            || request.StartDate.HasValue
            || request.EndDate.HasValue;
        var lowerInclusive = DateTimeOffset.MinValue;
        var upperExclusive = DateTimeOffset.MaxValue;

        if (request.DepartureDate.HasValue)
        {
            EnsureDateBounds(request.DepartureDate.Value, out lowerInclusive, out upperExclusive);
        }
        else
        {
            if (request.StartDate.HasValue)
            {
                EnsureDateBounds(request.StartDate.Value, out lowerInclusive, out _);
            }

            if (request.EndDate.HasValue)
            {
                EnsureDateBounds(request.EndDate.Value, out _, out upperExclusive);
            }
        }

        var snapshot = await dbContext.ExecuteInSerializableTransactionAsync(
            async transactionCancellationToken =>
            {
                var tours = dbContext.Tours
                    .AsNoTracking()
                    .Where(tour =>
                        (tour.Status == TourStatus.Published
                         || (tour.Status == TourStatus.Approved
                             && tour.PublishedAtUtc.HasValue
                             && tour.PublishedAtUtc.Value <= now))
                        && tour.OperatorProfile.ApprovalStatus == OperatorApprovalStatus.Approved
                        && tour.OperatorProfile.User.Status == AccountStatus.Active);

                if (destination is not null)
                {
                    tours = tours.Where(tour => tour.Destinations.Any(link =>
                        EF.Functions.Collate(link.Destination.Name, SearchCollation)
                            .Contains(destination)));
                }

                if (request.MinPrice.HasValue)
                {
                    tours = tours.Where(tour => tour.BasePrice >= request.MinPrice.Value);
                }

                if (request.MaxPrice.HasValue)
                {
                    tours = tours.Where(tour => tour.BasePrice <= request.MaxPrice.Value);
                }

                if (request.DurationDays.HasValue)
                {
                    tours = tours.Where(tour => tour.DurationDays == request.DurationDays.Value);
                }

                if (request.Category is not null)
                {
                    tours = tours.Where(tour => tour.Category != null
                        && tour.Category.IsActive
                        && tour.Category.Code == request.Category);
                }

                if (hasDates)
                {
                    tours = tours.Where(tour => tour.Schedules.Any(schedule =>
                        schedule.Status != TourScheduleStatus.Cancelled
                        && schedule.EndAtUtc > schedule.StartAtUtc
                        && schedule.StartAtUtc >= lowerInclusive
                        && schedule.StartAtUtc < upperExclusive));
                }

                foreach (var token in tokens)
                {
                    var searchToken = token;
                    tours = tours.Where(tour =>
                        EF.Functions.Collate(tour.Title, SearchCollation).Contains(searchToken)
                        || EF.Functions.Collate(
                            tour.OperatorProfile.CompanyName,
                            SearchCollation).Contains(searchToken)
                        || tour.Destinations.Any(link =>
                            EF.Functions.Collate(link.Destination.Name, SearchCollation)
                                .Contains(searchToken)));
                }

                var publishedRatings = PublicTourRatingProjection.Query(dbContext, tours);

                if (request.MinimumRating.HasValue)
                {
                    var minimum = request.MinimumRating.Value;
                    var ratedIds = publishedRatings
                        .Where(rating => rating.RawAverage >= minimum)
                        .Select(rating => rating.TourId);
                    tours = tours.Where(tour => ratedIds.Contains(tour.Id));
                }

                var totalCount = await tours.LongCountAsync(transactionCancellationToken);
                var candidates =
                    from tour in tours
                    join rating in publishedRatings
                        on tour.Id equals rating.TourId
                    from schedule in tour.Schedules
                        .Where(schedule => schedule.Status != TourScheduleStatus.Cancelled
                            && schedule.EndAtUtc > schedule.StartAtUtc
                            && (hasDates
                                ? schedule.StartAtUtc >= lowerInclusive
                                    && schedule.StartAtUtc < upperExclusive
                                : schedule.StartAtUtc >= now))
                        .OrderBy(schedule => schedule.StartAtUtc)
                        .ThenBy(schedule => schedule.Id)
                        .Take(1)
                        .DefaultIfEmpty()
                    select new SearchCandidate
                    {
                        Tour = tour,
                        Id = tour.Id,
                        Title = tour.Title,
                        OperatorName = tour.OperatorProfile.CompanyName,
                        DurationDays = tour.DurationDays,
                        BasePrice = tour.BasePrice,
                        RawAverage = rating.RawAverage,
                        ScheduleId = schedule == null ? null : (long?)schedule.Id,
                        DepartureAtUtc = schedule == null
                            ? null
                            : (DateTimeOffset?)schedule.StartAtUtc,
                    };

                var ordered = Order(candidates, resolvedSort, tokens);
                var offset = ((long)request.Page - 1) * request.PageSize;
                var pageRows = offset >= totalCount || offset > int.MaxValue
                    ? []
                    : await ordered.Skip((int)offset).Take(request.PageSize)
                        .ToArrayAsync(transactionCancellationToken);
                var pageIds = pageRows.Select(row => row.Id).ToArray();
                var allDestinations = pageIds.Length == 0
                    ? []
                    : await dbContext.TourDestinations
                        .AsNoTracking()
                        .Where(link => pageIds.Contains(link.TourId))
                        .OrderBy(link => link.TourId)
                        .ThenBy(link => link.SequenceNo)
                        .Select(link => new DestinationRow
                        {
                            TourId = link.TourId,
                            Name = link.Destination.Name,
                        })
                        .ToListAsync(transactionCancellationToken);
                var destinationsByTour = allDestinations
                    .GroupBy(link => link.TourId)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)group.Select(link => link.Name).ToArray());
                var thumbnailsByTour = pageIds.Length == 0
                    ? new Dictionary<long, string>()
                    : await dbContext.TourMedia
                        .AsNoTracking()
                        .Where(media => pageIds.Contains(media.TourId) && media.IsPrimary)
                        .Select(media => new { media.TourId, media.DeliveryUrl })
                        .ToDictionaryAsync(
                            media => media.TourId,
                            media => media.DeliveryUrl,
                            transactionCancellationToken);

                return new SearchPageSnapshot(
                    totalCount,
                    pageRows,
                    destinationsByTour,
                    thumbnailsByTour);
            },
            cancellationToken);

        // Availability is intentionally enriched only after the stable search transaction
        // commits. A schedule that is no longer independently readable degrades only that
        // Tour, while a provider/batch failure still propagates as the global 7.a1 path.
        var scheduleIds = snapshot.PageRows
            .Where(row => row.ScheduleId.HasValue)
            .Select(row => row.ScheduleId!.Value)
            .Distinct()
            .ToArray();
        var availabilityBySchedule = await availabilityReader.ReadAsync(
            scheduleIds,
            cancellationToken);
        var items = snapshot.PageRows.Select(row => MapItem(
                row,
                availabilityBySchedule,
                snapshot.DestinationsByTour.GetValueOrDefault(row.Id) ?? [],
                snapshot.ThumbnailsByTour.GetValueOrDefault(row.Id)))
            .ToArray();
        var totalPages = snapshot.TotalCount / request.PageSize
            + (snapshot.TotalCount % request.PageSize == 0 ? 0 : 1);

        return Result.Success(new PagedToursResponseDto(
            request.Page,
            request.PageSize,
            snapshot.TotalCount,
            totalPages,
            now.UtcDateTime,
            items));
    }

    private static IOrderedQueryable<SearchCandidate> Order(
        IQueryable<SearchCandidate> candidates,
        string sort,
        IReadOnlyList<string> tokens) => sort switch
        {
            SearchToursQuery.SortRelevance => OrderByRelevance(candidates, tokens)
                .ThenBy(candidate => candidate.RawAverage == null)
                .ThenByDescending(candidate => candidate.RawAverage)
                .ThenBy(candidate => EF.Functions.Collate(candidate.Title, SearchCollation))
                .ThenBy(candidate => candidate.Id),
            SearchToursQuery.SortPriceAscending => candidates
                .OrderBy(candidate => candidate.BasePrice)
                .ThenBy(candidate => EF.Functions.Collate(candidate.Title, SearchCollation))
                .ThenBy(candidate => candidate.Id),
            SearchToursQuery.SortPriceDescending => candidates
                .OrderByDescending(candidate => candidate.BasePrice)
                .ThenBy(candidate => EF.Functions.Collate(candidate.Title, SearchCollation))
                .ThenBy(candidate => candidate.Id),
            SearchToursQuery.SortRating => candidates
                .OrderBy(candidate => candidate.RawAverage == null)
                .ThenByDescending(candidate => candidate.RawAverage)
                .ThenBy(candidate => EF.Functions.Collate(candidate.Title, SearchCollation))
                .ThenBy(candidate => candidate.Id),
            _ => candidates
                .OrderBy(candidate => candidate.DepartureAtUtc == null)
                .ThenBy(candidate => candidate.DepartureAtUtc)
                .ThenBy(candidate => EF.Functions.Collate(candidate.Title, SearchCollation))
                .ThenBy(candidate => candidate.Id),
        };

    private static IOrderedQueryable<SearchCandidate> OrderByRelevance(
        IQueryable<SearchCandidate> candidates,
        IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return candidates.OrderBy(_ => 0);
        }

        // Sorting by descending cumulative thresholds is equivalent to sorting by the
        // weakest (maximum) per-token relevance class, while remaining SQL-translatable.
        var exact = AllTokens(tokens, 0);
        var prefix = AllTokens(tokens, 1);
        var title = AllTokens(tokens, 2);
        var destination = AllTokens(tokens, 3);
        return candidates.OrderByDescending(exact)
            .ThenByDescending(prefix)
            .ThenByDescending(title)
            .ThenByDescending(destination);
    }

    private static Expression<Func<SearchCandidate, bool>> AllTokens(
        IReadOnlyList<string> tokens,
        int maximumClass)
    {
        var parameter = Expression.Parameter(typeof(SearchCandidate), "candidate");
        Expression body = Expression.Constant(true);
        foreach (var token in tokens)
        {
            Expression<Func<SearchCandidate, bool>> tokenCondition = maximumClass switch
            {
                0 => candidate => EF.Functions.Collate(candidate.Tour.Title, SearchCollation) == token,
                1 => candidate => EF.Functions.Collate(candidate.Tour.Title, SearchCollation).StartsWith(token),
                2 => candidate => EF.Functions.Collate(candidate.Tour.Title, SearchCollation).Contains(token),
                _ => candidate => EF.Functions.Collate(candidate.Tour.Title, SearchCollation).Contains(token)
                    || candidate.Tour.Destinations.Any(link =>
                        EF.Functions.Collate(link.Destination.Name, SearchCollation).Contains(token)),
            };
            body = Expression.AndAlso(
                body,
                new ParameterReplacementVisitor(tokenCondition.Parameters[0], parameter)
                    .Visit(tokenCondition.Body)!);
        }

        return Expression.Lambda<Func<SearchCandidate, bool>>(body, parameter);
    }

    private sealed class ParameterReplacementVisitor(
        ParameterExpression source,
        ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == source ? target : base.VisitParameter(node);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim().Normalize(NormalizationForm.FormC);

    private static void EnsureDateBounds(
        DateOnly date,
        out DateTimeOffset lowerInclusive,
        out DateTimeOffset upperExclusive)
    {
        if (!TourSearchCriteria.TryGetDepartureBoundsUtc(
                date,
                out lowerInclusive,
                out upperExclusive))
        {
            throw new InvalidOperationException(
                "SearchToursQuery date bounds must be validated before the handler executes.");
        }
    }

    private TourSearchItemDto MapItem(
        SearchCandidate row,
        IReadOnlyDictionary<long, TourAvailabilityReadResult> availabilityBySchedule,
        IReadOnlyList<string> destinations,
        string? thumbnailUrl)
    {
        TourAvailabilityReadResult snapshot = default;
        var availabilityResolved = row.ScheduleId.HasValue
            && availabilityBySchedule.TryGetValue(row.ScheduleId.Value, out snapshot)
            && snapshot.IsResolved;
        var availability = row.ScheduleId switch
        {
            null => "noUpcomingSchedule",
            _ when !availabilityResolved => "unknown",
            _ when snapshot.ReservedCapacity < snapshot.TotalCapacity => "available",
            _ => "soldOut",
        };
        int? remainingSlots = availabilityResolved
            ? snapshot.TotalCapacity - snapshot.ReservedCapacity
            : null;
        var exposesBookableSchedule = row.ScheduleId.HasValue && availabilityResolved;

        if (row.ScheduleId.HasValue && !availabilityResolved)
        {
            logger.LogWarning(
                "TM-70 availability unavailable for tour {TourId}; returning a non-bookable result.",
                row.Id);
        }

        return new TourSearchItemDto(
            row.Id.ToString(CultureInfo.InvariantCulture),
            row.Title,
            destinations,
            row.OperatorName,
            row.DurationDays,
            checked((long)row.BasePrice),
            Currency,
            exposesBookableSchedule
                ? row.ScheduleId!.Value.ToString(CultureInfo.InvariantCulture)
                : null,
            exposesBookableSchedule ? row.DepartureAtUtc?.UtcDateTime : null,
            availability,
            remainingSlots,
            thumbnailUrl,
            TripReviewAggregateDto.Round(row.RawAverage));
    }

    private sealed class DestinationRow
    {
        public long TourId { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class SearchCandidate
    {
        public Tour Tour { get; init; } = null!;
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string OperatorName { get; init; } = string.Empty;
        public int DurationDays { get; init; }
        public decimal BasePrice { get; init; }
        public decimal? RawAverage { get; init; }
        public long? ScheduleId { get; init; }
        public DateTimeOffset? DepartureAtUtc { get; init; }
    }

    private sealed record SearchPageSnapshot(
        long TotalCount,
        IReadOnlyList<SearchCandidate> PageRows,
        IReadOnlyDictionary<long, IReadOnlyList<string>> DestinationsByTour,
        IReadOnlyDictionary<long, string> ThumbnailsByTour);
}