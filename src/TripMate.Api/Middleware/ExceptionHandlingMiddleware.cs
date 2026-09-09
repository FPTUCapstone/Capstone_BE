using System.Net;

using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
using ValidationException = TripMate.Application.Common.Exceptions.ValidationException;

namespace TripMate.Api.Middleware;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ValidationException ex)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/json";

            var response = ApiResponse<object>.ErrorResponse(
                (int)HttpStatusCode.BadRequest,
                "One or more validation errors occurred.",
                ex.Errors);

            await context.Response.WriteAsJsonAsync(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while processing {Method} {Path}",
                context.Request.Method, context.Request.Path);

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var response = ApiResponse<object>.ErrorResponse(
                (int)HttpStatusCode.InternalServerError,
                "An unexpected error occurred.",
                new { code = "MSG127" });

            await context.Response.WriteAsJsonAsync(response);
        }
    }
}