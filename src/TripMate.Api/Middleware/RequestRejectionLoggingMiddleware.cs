namespace TripMate.Api.Middleware;

/// <summary>
/// Logs 4xx rejection responses at Information level, recording only the HTTP status code.
/// Path, query string, and request headers are intentionally omitted to prevent token or
/// credential leakage into log aggregation pipelines (AGENTS.md §5.4 / BR-03).
/// </summary>
public class RequestRejectionLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestRejectionLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        var status = context.Response.StatusCode;
        if (status is >= 400 and <= 499)
        {
            // Only the numeric status is safe to log unconditionally:
            // path may embed user identifiers, query strings may contain tokens,
            // and the Authorization header is a direct credential.
            logger.LogInformation("Request rejected with status {StatusCode}.", status);
        }
    }
}