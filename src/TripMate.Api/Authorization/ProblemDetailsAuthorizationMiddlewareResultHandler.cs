using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Api.Authorization;

public sealed class ProblemDetailsAuthorizationMiddlewareResultHandler(
    ProblemDetailsFactory problemDetailsFactory,
    ILogger<ProblemDetailsAuthorizationMiddlewareResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        await RecordAuthorizationFailureAsync(context, authorizeResult);

        var authorizationMetadata = context.GetEndpoint()?.Metadata
            .GetMetadata<AuthorizationProblemDetailsAttribute>();

        if (authorizationMetadata is not null
            && (authorizeResult.Challenged || authorizeResult.Forbidden))
        {
            var status = authorizeResult.Challenged
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            var title = authorizeResult.Challenged
                ? authorizationMetadata.UnauthorizedTitle
                : authorizationMetadata.ForbiddenTitle;
            var errorCode = authorizeResult.Challenged
                ? authorizationMetadata.UnauthorizedErrorCode
                : authorizationMetadata.ForbiddenErrorCode;
            var authorizationProblem = problemDetailsFactory.CreateProblemDetails(
                context,
                status,
                title);
            authorizationProblem.Extensions["errorCode"] = errorCode;
            context.Response.StatusCode = status;

            await context.Response.WriteAsJsonAsync(
                authorizationProblem,
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
            return;
        }

        var responseMetadata = context.GetEndpoint()?.Metadata
            .GetMetadata<ForbiddenProblemDetailsAttribute>();

        if (!authorizeResult.Forbidden || responseMetadata is null)
        {
            await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var problemDetails = problemDetailsFactory.CreateProblemDetails(
            context,
            StatusCodes.Status403Forbidden,
            responseMetadata.Title);
        problemDetails.Extensions["errorCode"] = responseMetadata.ErrorCode;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        await context.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }

    private async Task RecordAuthorizationFailureAsync(
        HttpContext context,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden && !authorizeResult.Challenged)
        {
            return;
        }

        var metadata = context.GetEndpoint()?.Metadata
            .GetMetadata<AuthorizationFailureAuditAttribute>();
        if (metadata is null)
        {
            return;
        }

        try
        {
            long? targetId = TryGetRouteValueAsLong(
                context,
                metadata.AffectedEntityIdRouteValueName);
            long? actorUserId = TryGetActorUserId(context.User);
            string errorCode = authorizeResult.Challenged
                ? AuthorizationAuditErrorCodes.Unauthenticated
                : AuthorizationAuditErrorCodes.Forbidden;
            var recorder = context.RequestServices.GetService<IAuditFailureRecorder>();
            if (recorder is not null)
            {
                await recorder.RecordAsync(new AuditFailureEvent(
                    actorUserId,
                    metadata.ActionType,
                    metadata.AffectedEntity,
                    targetId,
                    errorCode));
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Authorization audit persistence failed for {Path}.",
                context.Request.Path);
        }
    }

    private static long? TryGetRouteValueAsLong(HttpContext context, string routeValueName) =>
        context.Request.RouteValues.TryGetValue(routeValueName, out object? rawValue)
        && long.TryParse(
            Convert.ToString(rawValue, CultureInfo.InvariantCulture),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long value)
        && value > 0
            ? value
            : null;

    private static long? TryGetActorUserId(ClaimsPrincipal user) =>
        long.TryParse(
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long value)
        && value > 0
            ? value
            : null;
}