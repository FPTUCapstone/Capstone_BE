using MediatR;
using Microsoft.AspNetCore.Mvc;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Api.Common;

[ApiController]
public abstract class ApiControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender { get; } = sender;

    protected ActionResult HandleFailure(Result result)
    {
        var statusCode = result.ErrorCode switch
        {
            AuthErrorCodes.InvalidCredentials => StatusCodes.Status401Unauthorized,
            AuthErrorCodes.AccountPendingVerification => StatusCodes.Status403Forbidden,
            AuthErrorCodes.AccountLocked => StatusCodes.Status403Forbidden,
            AuthErrorCodes.AccountInactive => StatusCodes.Status403Forbidden,
            AuthErrorCodes.EmailAlreadyRegistered => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Problem(
            title: result.ErrorMessage,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });
    }
}
