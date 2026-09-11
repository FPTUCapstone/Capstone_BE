using MediatR;
using Microsoft.AspNetCore.Mvc;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

using TripMate.Application.Features.Admin.TourOperatorApplications.Common;

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

            TourOperatorApplicationErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            TourOperatorApplicationErrorCodes.NotFound => StatusCodes.Status404NotFound,
            TourOperatorApplicationErrorCodes.NotPending => StatusCodes.Status409Conflict,
            TourOperatorApplicationErrorCodes.WrongRole => StatusCodes.Status422UnprocessableEntity,
            TourOperatorApplicationErrorCodes.Incomplete => StatusCodes.Status422UnprocessableEntity,
            TourOperatorApplicationErrorCodes.DocumentInvalid => StatusCodes.Status422UnprocessableEntity,
            TourOperatorApplicationErrorCodes.RejectionReasonRequired => StatusCodes.Status422UnprocessableEntity,
            TourOperatorApplicationErrorCodes.RejectionReasonTooLong => StatusCodes.Status422UnprocessableEntity,
            TourOperatorApplicationErrorCodes.NotImplemented => StatusCodes.Status501NotImplemented,

            _ => StatusCodes.Status400BadRequest,
        };

        return Problem(
            title: result.ErrorMessage,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });
    }
}
