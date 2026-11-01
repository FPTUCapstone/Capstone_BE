using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.PayoutSettlements.Authorize;

public sealed record AuthorizePayoutSettlementCommand(long PayoutId)
    : IRequest<Result<AuthorizePayoutSettlementResponseDto>>;

public sealed record AuthorizePayoutSettlementResponseDto(
    string PayoutId,
    string PayoutCode,
    string Status,
    DateTimeOffset AuthorizedAtUtc);
