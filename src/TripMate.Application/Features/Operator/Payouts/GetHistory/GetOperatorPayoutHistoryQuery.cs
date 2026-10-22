using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.GetHistory;

public sealed record GetOperatorPayoutHistoryQuery(int Page = 1, int PageSize = 20)
    : IRequest<Result<PayoutHistoryResponse>>;

public sealed record PayoutHistoryItemDto(
    string PayoutCode,
    int PeriodCount,
    string PeriodStart,
    string PeriodEnd,
    decimal PayableNetAmount,
    string Status,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? SettledAtUtc,
    string? RejectionReason,
    bool Cancellable);

public sealed record PayoutHistoryResponse(
    IReadOnlyList<PayoutHistoryItemDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);