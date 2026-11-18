using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingTours;

/// <summary>UC-60 Screen 13: Staff queue of tours waiting for review. Read-only.</summary>
public sealed record GetPendingToursQuery(
    string? Keyword = null,
    int PageNumber = 1,
    int PageSize = 20)
    : IRequest<Result<PaginatedList<PendingTourSummaryDto>>>
{
    public const int KeywordMaxLength = 100;
    public const int PageNumberMax = 1_000_000;
    public const int PageSizeMax = 100;
}

/// <summary>One queue row. Carries no operator contact data (phone, address, tax code, e-mail).</summary>
public sealed record PendingTourSummaryDto(
    long TourId,
    string TourName,
    string OperatorName,
    string Location,
    string? Category,
    DateTimeOffset SubmittedAtUtc,
    int DurationDays,
    decimal PriceVnd,
    DateTimeOffset? NextDepartureAtUtc,
    int? NextDepartureCapacity,
    int ImageCount);

public sealed class GetPendingToursQueryValidator : AbstractValidator<GetPendingToursQuery>
{
    public GetPendingToursQueryValidator()
    {
        RuleFor(x => x.PageNumber).InclusiveBetween(1, GetPendingToursQuery.PageNumberMax);
        RuleFor(x => x.PageSize).InclusiveBetween(1, GetPendingToursQuery.PageSizeMax);
        RuleFor(x => x.Keyword).MaximumLength(GetPendingToursQuery.KeywordMaxLength);
    }
}