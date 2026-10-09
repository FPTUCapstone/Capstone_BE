using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace TripMate.Api.Authorization;

public sealed class ProblemDetailsAuthorizationMiddlewareResultHandler(
    ProblemDetailsFactory problemDetailsFactory)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
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

            return context.Response.WriteAsJsonAsync(
                authorizationProblem,
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
        }

        var responseMetadata = context.GetEndpoint()?.Metadata
            .GetMetadata<ForbiddenProblemDetailsAttribute>();

        if (!authorizeResult.Forbidden || responseMetadata is null)
        {
            return _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
        }

        var problemDetails = problemDetailsFactory.CreateProblemDetails(
            context,
            StatusCodes.Status403Forbidden,
            responseMetadata.Title);
        problemDetails.Extensions["errorCode"] = responseMetadata.ErrorCode;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        return context.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }
}