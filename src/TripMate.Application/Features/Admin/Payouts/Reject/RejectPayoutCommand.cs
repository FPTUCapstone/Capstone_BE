using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Payouts.Reject;

public sealed record RejectPayoutCommand(long PayoutId, string? Reason)
    : IRequest<Result<RejectPayoutResponseDto>>;

public sealed record RejectPayoutResponseDto(
    string PayoutId,
    string Status,
    DateTimeOffset DecidedAtUtc,
    string RejectionReason);