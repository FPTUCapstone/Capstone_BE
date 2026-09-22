using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Features.Admin.SystemConfigs.Common;
using TripMate.Application.Features.Admin.SystemConfigs.GetAlgorithmParameters;
using TripMate.Application.Features.Admin.SystemConfigs.UpdateAlgorithmParameters;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "Administrator")]
[ForbiddenProblemDetails(AlgorithmConfigErrorCodes.Forbidden, "You do not have permission to access this function.")]
[Route("api/v1/admin/system-configs")]
public class AdminSystemConfigsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("algorithm-parameters")]
    public async Task<IActionResult> GetAlgorithmParameters(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetAlgorithmParametersQuery(), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("algorithm-parameters")]
    public async Task<IActionResult> UpdateAlgorithmParameters(
        [FromBody] UpdateAlgorithmParametersCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}