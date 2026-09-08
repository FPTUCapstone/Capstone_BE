using MediatR;
using Microsoft.AspNetCore.Mvc;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Api.Common;

[ApiController]
public abstract class ApiControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender { get; } = sender;

    protected ActionResult Success<T>(T data, int statusCode = StatusCodes.Status200OK, string message = "Success")
    {
        var response = ApiResponse<T>.SuccessResponse(data, statusCode, message);
        return StatusCode(statusCode, response);
    }

    protected ActionResult Error(int statusCode, string message, object? errors = null)
    {
        var response = ApiResponse<object>.ErrorResponse(statusCode, message, errors);
        return StatusCode(statusCode, response);
    }

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

        var errors = !string.IsNullOrWhiteSpace(result.ErrorCode)
            ? new { code = result.ErrorCode }
            : null;

        var response = ApiResponse<object>.ErrorResponse(
            statusCode,
            result.ErrorMessage ?? "An error occurred.",
            errors);

        return StatusCode(statusCode, response);
    }
}
