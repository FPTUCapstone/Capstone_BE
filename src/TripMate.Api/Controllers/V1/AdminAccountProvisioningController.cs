using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.AccountProvisioning;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Route("api/v1/admin")]
public sealed class AdminAccountProvisioningController(ISender sender) : ApiControllerBase(sender)
{
    [Authorize(Policy = UserRoleExtensions.AdministratorOnly)]
    [HttpPost("staff-accounts")]
    public async Task<IActionResult> CreateStaff(CreateStaffAccountCommand request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(request, cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : ProvisioningFailure(result);
    }

    [Authorize(Policy = UserRoleExtensions.StaffOnly)]
    [HttpPost("service-provider-accounts")]
    public async Task<IActionResult> CreateServiceProvider(CreateServiceProviderAccountCommand request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(request, cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : ProvisioningFailure(result);
    }

    [Authorize(Policy = UserRoleExtensions.StaffOnly)]
    [HttpGet("service-provider-accounts/available-pois")]
    public async Task<IActionResult> AvailablePois([FromQuery] GetAvailableProviderPoisQuery request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ProvisioningFailure(result);
    }
    private IActionResult ProvisioningFailure(Result result)
    {
        var statusCode = result.ErrorCode switch
        {
            ProvisioningErrors.Forbidden => StatusCodes.Status403Forbidden,
            ProvisioningErrors.DuplicateEmail or ProvisioningErrors.DuplicatePhone or
                ProvisioningErrors.PoiAlreadyLinked => StatusCodes.Status409Conflict,
            ProvisioningErrors.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        var extensions = new Dictionary<string, object?>(result.ErrorMetadata)
        {
            ["errorCode"] = result.ErrorCode,
        };
        return Problem(title: result.ErrorMessage, statusCode: statusCode, extensions: extensions);
    }
}