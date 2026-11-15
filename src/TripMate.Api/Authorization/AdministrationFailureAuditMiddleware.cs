using System.Security.Claims;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;

namespace TripMate.Api.Authorization;

/// <summary>Safe HTTP denial metadata; never record submitted reasons, search keywords or credentials.</summary>
public sealed class AdministrationFailureAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuditFailureRecorder recorder)
    {
        await next(context);
        if (context.Request.Path.StartsWithSegments("/api/v1/admin") && context.Response.StatusCode >= 400)
        {
            long? actor = long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : null;
            await recorder.RecordAsync(new(actor, AuditActionTypes.AdministrationAccessDenied,
                AuditEntityTypes.User, null, $"administration.http_{context.Response.StatusCode}"));
        }
    }
}