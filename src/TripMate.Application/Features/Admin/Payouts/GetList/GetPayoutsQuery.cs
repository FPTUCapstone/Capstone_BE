using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Admin.Payouts.GetList;

public sealed record GetPayoutsQuery(
    string? Keyword = null,
    string? Status = Payout.StatusPendingReview,
    DateOnly? PeriodFrom = null,
    DateOnly? PeriodTo = null,
    int PageNumber = 1,
    int PageSize = GetPayoutsQuery.DefaultPageSize)
    : IRequest<Result<PayoutsResponseDto>>
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumKeywordLength = 200;

    public static readonly string[] StaffVisibleStatuses =
    [
        Payout.StatusPendingReview,
        Payout.StatusApprovedForSettlement,
        Payout.StatusRejected,
    ];
}

public sealed record PayoutOperatorDto(
    string UserId,
    string CompanyName);

public sealed record PayoutListItemDto(
    string PayoutId,
    string PayoutCode,
    PayoutOperatorDto Operator,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int PeriodCount,
    decimal GrossRevenue,
    decimal CommissionAmount,
    decimal NetAmount,
    DateTimeOffset? RequestedAtUtc,
    string Status);

public sealed record PayoutsResponseDto(
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages,
    decimal TotalAmount,
    IReadOnlyList<PayoutListItemDto> Items);