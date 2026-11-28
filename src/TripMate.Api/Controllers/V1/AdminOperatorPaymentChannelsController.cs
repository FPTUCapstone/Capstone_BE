using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Approve;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Disable;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.List;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Administrator))]
[Route("api/v1/admin/operator-payment-channels")]
public sealed class AdminOperatorPaymentChannelsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List(
        CancellationToken cancellationToken,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await Sender.Send(
            new ListOperatorPaymentChannelsQuery(status, page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record ApproveChannelRequest(Guid OperationId, int ExpectedVersion, string Reason);

    [HttpPost("{channelId:long}/approve")]
    public async Task<IActionResult> Approve(
        long channelId,
        [FromBody] ApproveChannelRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new ApproveOperatorPaymentChannelCommand(
                channelId, request.OperationId, request.ExpectedVersion, request.Reason),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record DisableChannelRequest(Guid OperationId, int ExpectedVersion, string Reason);

    [HttpPost("{channelId:long}/disable")]
    public async Task<IActionResult> Disable(
        long channelId,
        [FromBody] DisableChannelRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new DisableOperatorPaymentChannelCommand(
                channelId, request.OperationId, request.ExpectedVersion, request.Reason),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}