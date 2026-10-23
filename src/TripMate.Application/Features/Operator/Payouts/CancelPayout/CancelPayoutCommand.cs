using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.CancelPayout;

public sealed record CancelPayoutCommand(string PayoutCode) : IRequest<Result<CancelPayoutResultDto>>;

public sealed record CancelPayoutResultDto(string PayoutCode, string Status);