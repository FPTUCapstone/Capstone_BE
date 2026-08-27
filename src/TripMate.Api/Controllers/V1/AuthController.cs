using MediatR;
using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
using TripMate.Application.Features.Authentication.Login;
using TripMate.Application.Features.Authentication.Register;

namespace TripMate.Api.Controllers.V1;

[Route("api/v1/auth")]
public class AuthController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterTravelerCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginCommand command, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
