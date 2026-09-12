using System.Net;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using TripMate.Api.Common;

using ValidationException = TripMate.Application.Common.Exceptions.ValidationException;

namespace TripMate.Api.Middleware;

public class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IOptions<JsonOptions> jsonOptions)
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
            context.Response.ContentType = "application/problem+json";

            var serializerOptions = jsonOptions.Value.JsonSerializerOptions;
            var problem = new ValidationProblemDetails(
                ValidationErrorKeyNormalizer.Normalize(
                    ex.Errors,
                    serializerOptions.PropertyNamingPolicy))
            {
                Title = "One or more validation errors occurred.",
                Status = (int)HttpStatusCode.BadRequest,
            };

            await context.Response.WriteAsJsonAsync(
                problem,
                options: serializerOptions,
                contentType: "application/problem+json");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while processing {Method} {Path}",
                context.Request.Method, context.Request.Path);

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Title = "An unexpected error occurred.",
                Status = (int)HttpStatusCode.InternalServerError,
            };

            await context.Response.WriteAsJsonAsync(
                problem,
                options: jsonOptions.Value.JsonSerializerOptions,
                contentType: "application/problem+json");
        }
    }
}