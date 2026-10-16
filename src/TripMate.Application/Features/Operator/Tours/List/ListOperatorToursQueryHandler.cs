using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.List;

public sealed class ListOperatorToursQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<ListOperatorToursQuery, Result<OperatorTourListResponse>>
{
    public async Task<Result<OperatorTourListResponse>> Handle(
        ListOperatorToursQuery request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue)
        {
            return AccessDenied();
        }

        long operatorId = currentUser.UserId.Value;
        bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId &&
            profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator &&
            profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible)
        {
            return AccessDenied();
        }

        int page = Math.Max(1, request.Page);
        int pageSize = Math.Clamp(request.PageSize, 1, ListOperatorToursQuery.MaxPageSize);
        var query = dbContext.Tours.AsNoTracking().Where(tour => tour.OperatorUserId == operatorId);
        if (ListOperatorToursQueryValidator.TryParseStatus(request.Status, out TourStatus status))
        {
            query = query.Where(tour => tour.Status == status);
        }

        int totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(tour => tour.UpdatedAtUtc)
            .ThenByDescending(tour => tour.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tour => new OperatorTourListItemDto(
                tour.Id,
                tour.Title,
                tour.Status.ToString(),
                tour.BasePrice,
                tour.DurationDays,
                tour.VersionNo,
                tour.UpdatedAtUtc,
                tour.Schedules
                    .Where(schedule => schedule.Status == TourScheduleStatus.Scheduled)
                    .OrderBy(schedule => schedule.StartAtUtc)
                    .Select(schedule => (DateTimeOffset?)schedule.StartAtUtc)
                    .FirstOrDefault(),
                tour.DestinationSummary))
            .ToListAsync(cancellationToken);
        return Result.Success(new OperatorTourListResponse(items, page, pageSize, totalCount));
    }

    private static Result<OperatorTourListResponse> AccessDenied() =>
        Result.Failure<OperatorTourListResponse>(
            OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}
