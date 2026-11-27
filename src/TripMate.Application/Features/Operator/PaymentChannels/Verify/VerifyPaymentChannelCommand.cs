using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;

namespace TripMate.Application.Features.Operator.PaymentChannels.Verify;

public sealed record VerifyPaymentChannelCommand : IRequest<Result<ChannelStatusDto>>;
