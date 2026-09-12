using MediatR;

using Microsoft.AspNetCore.Mvc;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.PointsOfInterest.Common;

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
            PoiErrorCodes.AdminAccessRequired => StatusCodes.Status403Forbidden,
            PoiErrorCodes.ReferenceNotFound => StatusCodes.Status404NotFound,
            PoiErrorCodes.PossibleDuplicate => StatusCodes.Status409Conflict,
            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.ItineraryNotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };

        var extensions = new Dictionary<string, object?>(result.ErrorMetadata)
        {
            ["errorCode"] = result.ErrorCode,
        };


        return Problem(
            title: result.ErrorMessage,
            statusCode: statusCode,
            extensions: extensions);
    }
}