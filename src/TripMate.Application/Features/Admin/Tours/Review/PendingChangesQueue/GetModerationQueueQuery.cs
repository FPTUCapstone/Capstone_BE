using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingChangesQueue;

/// <summary>
/// One logical moderation queue combining first-time Pending tours and PendingReview draft
/// versions (spec TM-82 §3 point E2/§5.6, UC-37 PC-02's "handled by UC-60 or UC-61"). Additive:
/// does not change GetTourReviewQueueQuery's own contract.
/// </summary>
public sealed record GetModerationQueueQuery(int Page = 1, int PageSize = GetModerationQueueQuery.DefaultPageSize)
    : IRequest<Result<ModerationQueueResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

public static class ModerationQueueKinds
{
    public const string NewSubmission = "NewSubmission";
    public const string DraftVersion = "DraftVersion";
}

public sealed record ModerationQueueItem(
    long Id,
    long TourId,
    string Kind,
    long OperatorUserId,
    string Title,
    string Status,
    DateTimeOffset? SubmittedAtUtc);

public sealed record ModerationQueueResponse(
    IReadOnlyList<ModerationQueueItem> Items, int Page, int PageSize, int TotalCount);

public sealed class GetModerationQueueQueryValidator : AbstractValidator<GetModerationQueueQuery>
{
    public GetModerationQueueQueryValidator()
    {
        RuleFor(query => query.Page).InclusiveBetween(1, int.MaxValue / GetModerationQueueQuery.MaxPageSize);
        RuleFor(query => query.PageSize).InclusiveBetween(1, GetModerationQueueQuery.MaxPageSize);
    }
}
