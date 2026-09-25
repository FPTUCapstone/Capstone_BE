using MediatR;

using Microsoft.AspNetCore.Mvc;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Api.Common;

[ApiController]
public abstract class ApiControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender { get; } = sender;

    protected ActionResult Success<T>(
        T data,
        int statusCode = StatusCodes.Status200OK,
        string message = "Success")
    {
        var response = ApiResponse<T>.SuccessResponse(
            data,
            statusCode,
            message);

        return StatusCode(statusCode, response);
    }

    protected ActionResult Error(
        int statusCode,
        string message,
        object? errors = null)
    {
        var response = ApiResponse<object>.ErrorResponse(
            statusCode,
            message,
            errors);

        return StatusCode(statusCode, response);
    }

    protected ActionResult HandleFailure(Result result)
    {
        var statusCode = result.ErrorCode switch
        {
            AuthErrorCodes.InvalidCredentials =>
                StatusCodes.Status401Unauthorized,

            AuthErrorCodes.AuthTokenInvalid =>
                StatusCodes.Status401Unauthorized,

            AuthErrorCodes.Msg14 =>
                StatusCodes.Status401Unauthorized,

            AuthErrorCodes.AccountPendingVerification =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountStateUnresolved =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountPendingApproval =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.MsgEmailNotVerified =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountLocked =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountInactive =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AdminAccessRequired =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AdminGoogleSignInDisabled =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AdminMobileSignInDisabled =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.VerificationUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            AuthErrorCodes.FirebaseUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            AuthErrorCodes.EmailAlreadyRegistered =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Admin.AuditLogs.Common.AuditLogErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.AuditLogs.Common.AuditLogErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.NotPending =>
                StatusCodes.Status409Conflict,

            PoiErrorCodes.AdminAccessRequired =>
                StatusCodes.Status403Forbidden,

            PoiErrorCodes.ReferenceNotFound =>
                StatusCodes.Status404NotFound,

            PoiErrorCodes.PossibleDuplicate =>
                StatusCodes.Status409Conflict,

            PoiErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.ItineraryNotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.GroupNotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.HostPermissionRequired =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.AlreadyActiveMember =>
                StatusCodes.Status409Conflict,

            SchedulingErrorCodes.IdempotencyKeyPayloadMismatch =>
                StatusCodes.Status409Conflict,

            SchedulingErrorCodes.ConstraintsInfeasible =>
                StatusCodes.Status422UnprocessableEntity,

            _ => StatusCodes.Status400BadRequest,
        };

        var extensions = new Dictionary<string, object?>(
            result.ErrorMetadata)
        {
            ["errorCode"] = result.ErrorCode,
        };

        return Problem(
            title: result.ErrorMessage,
            statusCode: statusCode,
            extensions: extensions);
    }
}