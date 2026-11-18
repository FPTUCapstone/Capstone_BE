using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.Authorization;

public static class UserSessionValidation
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!long.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role))
        {
            context.Fail("The session is unavailable.");
            return;
        }
        var claimedVersion = principal.FindFirstValue(User.SessionVersionClaim);
        var version = 0; // Pre-TM-228 signed tokens are valid only until the first revocation.
        if (claimedVersion is not null && (!int.TryParse(claimedVersion, out version) || version < 0))
        {
            context.Fail("The session is unavailable.");
            return;
        }
        var db = context.HttpContext.RequestServices.GetRequiredService<IApplicationDbContext>();
        var account = await db.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.Role, user.Status, user.SessionVersion })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        if (account is null || account.Role != role || account.SessionVersion != version
            || account.Status == AccountStatus.Locked
            || (role.IsAdministrationRole() && account.Status != AccountStatus.Active)
            || (role == UserRole.ServiceProvider && account.Status != AccountStatus.Active))
            context.Fail("The session is unavailable.");
    }
}