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