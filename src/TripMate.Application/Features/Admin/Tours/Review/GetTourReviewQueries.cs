using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.Get;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Tours.Review;

/// <summary>
/// Staff review queue. <paramref name="Keyword"/> (UC-60 Screen 13, CR-02) matches the tour title or the operator
/// company name literally (%, _ and [ are not wildcards).
/// </summary>
public sealed record GetTourReviewQueueQuery(
    int Page = 1,
    int PageSize = GetTourReviewQueueQuery.DefaultPageSize,
    string? Keyword = null)
    : IRequest<Result<TourReviewQueueResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int KeywordMaxLength = 100;
}

/// <summary>
/// The first six members are the original TM-83 contract. The rest are the Screen 13 columns, added with defaults so
/// existing callers keep working; none of them exposes operator contact data.
/// </summary>
public sealed record TourReviewQueueItem(long Id, long OperatorUserId, string Title, string Status,
    DateTimeOffset? SubmittedAtUtc, string? SubmissionNote,
    string? OperatorName = null, string? Location = null, string? Category = null,
    int DurationDays = 0, decimal PriceVnd = 0m,
    DateTimeOffset? NextDepartureAtUtc = null, int? NextDepartureCapacity = null, int ImageCount = 0);
public sealed record TourReviewQueueResponse(IReadOnlyList<TourReviewQueueItem> Items, int Page, int PageSize, int TotalCount);
public sealed record GetTourReviewDetailQuery(long TourId) : IRequest<Result<OperatorTourDetailDto>>;

public sealed class GetTourReviewQueueQueryValidator : AbstractValidator<GetTourReviewQueueQuery>
{
    public GetTourReviewQueueQueryValidator()
    {
        RuleFor(query => query.Page).InclusiveBetween(1, int.MaxValue / GetTourReviewQueueQuery.MaxPageSize);
        RuleFor(query => query.PageSize).InclusiveBetween(1, GetTourReviewQueueQuery.MaxPageSize);
        RuleFor(query => query.Keyword).MaximumLength(GetTourReviewQueueQuery.KeywordMaxLength);
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
        var keyword = request.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            // The reviewer's text is a literal search term: %, _ and [ must not act as wildcards.
            var pattern = $"%{EscapeLike(keyword)}%";
            pending = pending.Where(tour =>
                EF.Functions.Like(tour.Title, pattern, LikeEscape)
                || EF.Functions.Like(tour.OperatorProfile.CompanyName, pattern, LikeEscape));
        }

        int count = await pending.CountAsync(cancellationToken);
        var items = await pending.OrderBy(tour => tour.SubmittedAtUtc).ThenBy(tour => tour.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(tour => new TourReviewQueueItem(tour.Id, tour.OperatorUserId, tour.Title,
                tour.Status.ToString(), tour.SubmittedAtUtc, tour.SubmissionNote,
                tour.OperatorProfile.CompanyName,
                tour.DestinationSummary,
                tour.Category != null ? tour.Category.Name : null,
                tour.DurationDays,
                tour.BasePrice,
                tour.Schedules.OrderBy(s => s.StartAtUtc).Select(s => (DateTimeOffset?)s.StartAtUtc).FirstOrDefault(),
                tour.Schedules.OrderBy(s => s.StartAtUtc).Select(s => (int?)s.TotalCapacity).FirstOrDefault(),
                tour.Media.Count(m => m.LifecycleStatus == TourMediaLifecycleStatus.Active && m.DeletedAtUtc == null)))
            .ToListAsync(cancellationToken);
        return Result.Success(new TourReviewQueueResponse(items, request.Page, request.PageSize, count));
    }

    private const string LikeEscape = "\\";

    private static string EscapeLike(string value) => value
        .Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
        .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
        .Replace("_", LikeEscape + "_", StringComparison.Ordinal)
        .Replace("[", LikeEscape + "[", StringComparison.Ordinal);
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