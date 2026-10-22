using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Payouts.Approve;

public sealed record ApprovePayoutCommand(long PayoutId) : IRequest<Result<ApprovePayoutResponseDto>>;

public sealed record ApprovePayoutResponseDto(
    string PayoutId,
    string Status,
    DateTimeOffset DecidedAtUtc);
