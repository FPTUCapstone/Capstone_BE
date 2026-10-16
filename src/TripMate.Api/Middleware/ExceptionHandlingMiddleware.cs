using System.Net;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;

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

            AddActiveTripsErrorCode(context, problem, ex.ErrorCodes.Values.SelectMany(code => code));

            await context.Response.WriteAsJsonAsync(
                problem,
                options: serializerOptions,
                contentType: "application/problem+json");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            context.Response.StatusCode =
                (int)HttpStatusCode.InternalServerError;

            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Title = "Hệ thống chưa thể hoàn tất yêu cầu. Vui lòng thử lại.",
                Status = (int)HttpStatusCode.InternalServerError,
            };

            await context.Response.WriteAsJsonAsync(
                problem,
                options: jsonOptions.Value.JsonSerializerOptions,
                contentType: "application/problem+json");
        }
    }

    /// <summary>
    /// Attaches a stable <c>errorCode</c> extension to a 400 ValidationProblemDetails body.
    /// Active Trips keeps its own historical heuristic (not every one of its rules carries
    /// <c>WithErrorCode</c>, so a path-specific fallback fills the gap). Every other feature gets
    /// a generic rule instead: if any failed FluentValidation rule set an explicit
    /// <c>WithErrorCode</c>, the first one found is used. A validator with no
    /// <c>WithErrorCode</c> anywhere still gets a plain ValidationProblemDetails with no
    /// errorCode, same as before this method existed for it.
    /// </summary>
    internal static void AddActiveTripsErrorCode(
        HttpContext context,
        ProblemDetails problem,
        IEnumerable<string>? validationErrorCodes = null)
    {
        var codes = (validationErrorCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();

        if (context.Request.Path.StartsWithSegments("/api/v1/admin/trips/active"))
        {
            var validation = problem as ValidationProblemDetails;
            var hasDateError = codes.Contains(ActiveTripErrorCodes.InvalidDateRange, StringComparer.Ordinal) ||
                validation?.Errors.Keys.Any(key =>
                    string.Equals(key, "startDateFrom", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "startDateTo", StringComparison.OrdinalIgnoreCase)) == true;

            problem.Extensions["errorCode"] = hasDateError
                ? ActiveTripErrorCodes.InvalidDateRange
                : ActiveTripErrorCodes.InvalidFilter;
            return;
        }

        if (codes.Length > 0)
        {
            problem.Extensions["errorCode"] = codes[0];
        }
    }
}
