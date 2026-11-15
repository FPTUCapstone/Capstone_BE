using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.AccountProvisioning;

namespace TripMate.Api.Middleware;

/// <summary>Register immediately after authentication. Reads persisted state on every request.</summary>
public sealed class RequirePasswordChangeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApplicationDbContext db, ICurrentUserService currentUser)
    {
        var path = context.Request.Path.Value?.TrimEnd('/');
        var canChangeOrSignOut = HttpMethods.IsPost(context.Request.Method)
            && (string.Equals(path, "/api/v1/auth/change-password", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/v1/auth/web/change-password", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/v1/auth/web/logout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/v1/auth/web/refresh", StringComparison.OrdinalIgnoreCase));
        if (context.User.Identity?.IsAuthenticated == true && currentUser.UserId is { } userId
            && !canChangeOrSignOut
            && await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.MustChangePassword,
                context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Change your temporary password before continuing.",
                Extensions = { ["errorCode"] = ProvisioningErrors.PasswordChangeRequired, ["mustChangePassword"] = true },
            }, options: (System.Text.Json.JsonSerializerOptions?)null,
                contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }
}