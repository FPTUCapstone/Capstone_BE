using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;

namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.Disable;

public sealed record DisableOperatorPaymentChannelCommand(
    long ChannelId,
    Guid OperationId,
    int ExpectedVersion,
    string Reason)
    : IRequest<Result<ChannelStatusDto>>;