using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;

namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.Approve;

public sealed record ApproveOperatorPaymentChannelCommand(
    long ChannelId,
    Guid OperationId,
    int ExpectedVersion,
    string Reason)
    : IRequest<Result<ChannelStatusDto>>;
