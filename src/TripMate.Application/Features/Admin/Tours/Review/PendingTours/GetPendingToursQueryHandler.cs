using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingTours;

public sealed class GetPendingToursQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetPendingToursQuery, Result<PaginatedList<PendingTourSummaryDto>>>
{
    private const string LikeEscape = "\\";

    public async Task<Result<PaginatedList<PendingTourSummaryDto>>> Handle(
        GetPendingToursQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff))
        {
            return Result.Failure<PaginatedList<PendingTourSummaryDto>>(
                OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");
        }

        var query = dbContext.Tours.AsNoTracking().Where(tour => tour.Status == TourStatus.Pending);

        var keyword = request.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            // The reviewer's text is a literal search term: %, _ and [ must not act as wildcards.
            var pattern = $"%{EscapeLike(keyword)}%";
            query = query.Where(tour =>
                EF.Functions.Like(tour.Title, pattern, LikeEscape)
                || EF.Functions.Like(tour.OperatorProfile.CompanyName, pattern, LikeEscape));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Oldest submission first (UpdatedAtUtc is stamped by SubmitForReview and a Pending tour is frozen
        // for its operator); Id breaks ties so paging is deterministic.
        var items = await query
            .OrderBy(tour => tour.UpdatedAtUtc)
            .ThenBy(tour => tour.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(tour => new PendingTourSummaryDto(
                tour.Id,
                tour.Title,
                tour.OperatorProfile.CompanyName,
                tour.DestinationSummary,
                tour.Category != null ? tour.Category.Name : null,
                tour.UpdatedAtUtc,
                tour.DurationDays,
                tour.BasePrice,
                tour.Schedules.OrderBy(s => s.StartAtUtc).Select(s => (DateTimeOffset?)s.StartAtUtc).FirstOrDefault(),
                tour.Schedules.OrderBy(s => s.StartAtUtc).Select(s => (int?)s.TotalCapacity).FirstOrDefault(),
                tour.Media.Count(m => m.LifecycleStatus == TourMediaLifecycleStatus.Active && m.DeletedAtUtc == null)))
            .ToListAsync(cancellationToken);

        return Result.Success(new PaginatedList<PendingTourSummaryDto>(items, totalCount, request.PageNumber, request.PageSize));
    }

    private static string EscapeLike(string value) => value
        .Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
        .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
        .Replace("_", LikeEscape + "_", StringComparison.Ordinal)
        .Replace("[", LikeEscape + "[", StringComparison.Ordinal);
}