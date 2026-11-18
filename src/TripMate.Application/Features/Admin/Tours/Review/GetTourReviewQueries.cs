using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.Get;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Tours.Review;

public sealed record GetTourReviewQueueQuery(int Page = 1, int PageSize = GetTourReviewQueueQuery.DefaultPageSize)
    : IRequest<Result<TourReviewQueueResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

public sealed record TourReviewQueueItem(long Id, long OperatorUserId, string Title, string Status,
    DateTimeOffset? SubmittedAtUtc, string? SubmissionNote);
public sealed record TourReviewQueueResponse(IReadOnlyList<TourReviewQueueItem> Items, int Page, int PageSize, int TotalCount);
public sealed record GetTourReviewDetailQuery(long TourId) : IRequest<Result<OperatorTourDetailDto>>;

public sealed class GetTourReviewQueueQueryValidator : AbstractValidator<GetTourReviewQueueQuery>
{
    public GetTourReviewQueueQueryValidator()
    {
        RuleFor(query => query.Page).InclusiveBetween(1, int.MaxValue / GetTourReviewQueueQuery.MaxPageSize);
        RuleFor(query => query.PageSize).InclusiveBetween(1, GetTourReviewQueueQuery.MaxPageSize);
    }
}

internal static class TourReviewReadAccess
{
    public static Task<bool> IsActiveStaffAsync(IApplicationDbContext db, ICurrentUserService user, CancellationToken ct) =>
        user.UserId.HasValue && user.Role == nameof(UserRole.Staff)
            ? db.Users.AsNoTracking().AnyAsync(account => account.Id == user.UserId.Value
                && account.Role == UserRole.Staff && account.Status == AccountStatus.Active, ct)
            : Task.FromResult(false);
}

public sealed class GetTourReviewQueueQueryHandler(IApplicationDbContext db, ICurrentUserService user)
    : IRequestHandler<GetTourReviewQueueQuery, Result<TourReviewQueueResponse>>
{
    public async Task<Result<TourReviewQueueResponse>> Handle(GetTourReviewQueueQuery request, CancellationToken cancellationToken)
    {
        if (!await TourReviewReadAccess.IsActiveStaffAsync(db, user, cancellationToken))
            return Result.Failure<TourReviewQueueResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Active Staff access is required.");
        var pending = db.Tours.AsNoTracking().Where(tour => tour.Status == TourStatus.Pending);
        int count = await pending.CountAsync(cancellationToken);
        var items = await pending.OrderBy(tour => tour.SubmittedAtUtc).ThenBy(tour => tour.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(tour => new TourReviewQueueItem(tour.Id, tour.OperatorUserId, tour.Title,
                tour.Status.ToString(), tour.SubmittedAtUtc, tour.SubmissionNote)).ToListAsync(cancellationToken);
        return Result.Success(new TourReviewQueueResponse(items, request.Page, request.PageSize, count));
    }
}

public sealed class GetTourReviewDetailQueryHandler(IApplicationDbContext db, ICurrentUserService user, IDateTimeProvider clock)
    : IRequestHandler<GetTourReviewDetailQuery, Result<OperatorTourDetailDto>>
{
    public async Task<Result<OperatorTourDetailDto>> Handle(GetTourReviewDetailQuery request, CancellationToken cancellationToken)
    {
        if (!await TourReviewReadAccess.IsActiveStaffAsync(db, user, cancellationToken))
            return Result.Failure<OperatorTourDetailDto>(OperatorTourErrorCodes.AdminAccessRequired, "Active Staff access is required.");
        var result = await new GetOperatorTourQueryHandler(db, user, clock).ReadDetailAsync(request.TourId, null, cancellationToken);
        return result.IsSuccess ? Result.Success(result.Value with { CanSubmitForReview = false }) : result;
    }
}