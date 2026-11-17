using System.Reflection;
using System.Security.Claims;

using Microsoft.AspNetCore.Http;

using TripMate.Api.Middleware;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.AccountProvisioning;

public sealed class PasswordChangeMiddlewareTests
{
    [Theory]
    [InlineData(nameof(UserRole.Traveler))]
    [InlineData(nameof(UserRole.TourOperator))]
    [InlineData(nameof(UserRole.Administrator))]
    public async Task ExistingRolesReachTheirEndpointWithoutANewSqlPrecondition(string role)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "10"), new Claim(ClaimTypes.Role, role)], "test"));
        var database = DispatchProxy.Create<IApplicationDbContext, UnexpectedDatabaseAccess>();
        var reached = false;
        var middleware = new RequirePasswordChangeMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, database, new Actor(role));
        Assert.True(reached);
    }

    private sealed class Actor(string role) : ICurrentUserService
    {
        public long? UserId => 10;
        public string? Role => role;
    }

    public class UnexpectedDatabaseAccess : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("The unrelated endpoint must own its SQL availability contract.");
    }
}