using System.ComponentModel.DataAnnotations;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.PushDevices;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/me/push-devices")]
public sealed class PushDevicesController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPut("{installationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Register(
        Guid installationId,
        [FromBody, Required] RegisterPushDeviceRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new RegisterPushDeviceCommand(
                currentUserService.UserId.Value,
                installationId,
                request.Token),
            cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }

    [HttpDelete("{installationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid installationId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new RemovePushDeviceCommand(currentUserService.UserId.Value, installationId),
            cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }
}

public sealed record RegisterPushDeviceRequest(string Token);