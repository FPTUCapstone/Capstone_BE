using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;

namespace TripMate.Application.Features.Operator.PaymentChannels.GetChannel;

public sealed record GetOperatorPaymentChannelQuery : IRequest<Result<ChannelStatusDto>>;