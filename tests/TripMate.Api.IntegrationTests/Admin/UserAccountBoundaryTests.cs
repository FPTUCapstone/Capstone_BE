using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

[Collection(nameof(TripMateApiFactory))]
public sealed class UserAccountBoundaryTests
{
    [Theory]
    [InlineData("/api/v1/admin/tour-operator-applications/9999", false)]
    [InlineData("/api/v1/admin/tours/9999/approve", true)]
    [InlineData("/api/v1/admin/trips/active/9999", false)]
    public async Task Operations_StaffCanReachHandlerAndAdministratorIsDenied(string path, bool post)
    {
        await using var factory = await SeedAsync();
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var admin = factory.CreateAuthenticatedClient(20, UserRole.Administrator);
        using var staffResponse = post ? await staff.PostAsync(path, null) : await staff.GetAsync(path);
        using var adminResponse = post ? await admin.PostAsync(path, null) : await admin.GetAsync(path);
        staffResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lock_InvalidatesSignedAccessTokensEvenAfterUnlock()
    {
        await using var factory = await SeedAsync(ApiTestAuthenticationMode.JwtBearer);
        async Task<string> Token(long userId)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
            return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().GenerateAccessToken(user).Token;
        }
        using var staff = factory.CreateJwtClient(await Token(10));
        using var administrator = factory.CreateJwtClient(await Token(20));
        (await staff.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await administrator.PostAsJsonAsync("/api/v1/admin/users/10/lock", new { reason = "Policy" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await administrator.PostAsJsonAsync("/api/v1/admin/users/10/unlock", new { reason = "Resolved" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var fresh = factory.CreateJwtClient(await Token(10));
        (await fresh.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(UserRole.Staff, 10, "Traveler", "TourOperator")]
    [InlineData(UserRole.Administrator, 20, "Staff", "Staff")]
    public async Task List_FiltersByAuthoritativeRoleBeforeCountAndPage(UserRole role, long actorId, string first, string second)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(actorId, role);
        using var response = await client.GetAsync("/api/v1/admin/users");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("role").GetString())
            .Should().OnlyContain(value => value == first || value == second);
        json.GetProperty("totalCount").GetInt32().Should().Be(role == UserRole.Staff ? 2 : 1);
        json.ToString().Should().NotContain("passwordHash").And.NotContain("TokenHash");
    }

    [Theory]
    [InlineData(UserRole.Staff, 10, 20)]
    [InlineData(UserRole.Administrator, 20, 30)]
    [InlineData(UserRole.Administrator, 20, 40)]
    public async Task Detail_OutOfScopeAndMissingAreIndistinguishable(UserRole role, long actor, long target)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(actor, role);
        using var forbidden = await client.GetAsync($"/api/v1/admin/users/{target}");
        using var missing = await client.GetAsync("/api/v1/admin/users/9999");
        forbidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(forbidden.StatusCode);
        var left = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync()).RootElement;
        var right = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        left.GetProperty("errorCode").GetString().Should().Be(right.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("lock", "")]
    [InlineData("unlock", " ")]
    public async Task Mutation_RequiresReason(string action, string reason)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var response = await client.PostAsJsonAsync($"/api/v1/admin/users/30/{action}", new { reason });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lock_RevokesRefreshTokensAndUnlockRestoresPendingStatusWithAudit()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var target = await db.Users.SingleAsync(user => user.Id == 30);
            target.Status = AccountStatus.PendingEmailVerification;
            db.RefreshTokens.Add(new RefreshToken { User = target, TokenHash = "private-token-hash", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var locked = await client.PostAsJsonAsync("/api/v1/admin/users/30/lock", new { reason = "Policy violation" });
        locked.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Status.Should().Be(AccountStatus.Locked);
            (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().NotBeNull();
            return true;
        });
        using var unlocked = await client.PostAsJsonAsync("/api/v1/admin/users/30/unlock", new { reason = "Resolved" });
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Status.Should().Be(AccountStatus.PendingEmailVerification);
            (await db.AuditLogs.CountAsync(row => row.AffectedEntityId == 30 && row.Result == AuditOutcome.Success)).Should().Be(2);
            return true;
        });
    }

    [Fact]
    public async Task Mutation_CannotChangeRole()
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var response = await client.PostAsJsonAsync("/api/v1/admin/users/30/lock", new { reason = "Policy", role = "Administrator" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Role.Should().Be(UserRole.Traveler);
            return true;
        });
    }

    [Theory]
    [InlineData(UserRole.Staff, 10, 20)]
    [InlineData(UserRole.Administrator, 20, 30)]
    public async Task Lock_CannotCrossRoleBoundary(UserRole role, long actor, long target)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(actor, role);
        using var response = await client.PostAsJsonAsync($"/api/v1/admin/users/{target}/lock", new { reason = "Policy" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == target)).Status.Should().Be(AccountStatus.Active);
            return true;
        });
    }

    private static async Task<TripMateApiFactory> SeedAsync(ApiTestAuthenticationMode mode = ApiTestAuthenticationMode.HeaderStub)
    {
        var factory = new TripMateApiFactory(mode);
        await factory.WithDbContextAsync(async db =>
        {
            foreach (var (id, role) in new[] { (10L, UserRole.Staff), (20L, UserRole.Administrator), (30L, UserRole.Traveler), (40L, UserRole.TourOperator) })
                db.Users.Add(new User { Id = id, Role = role, Status = AccountStatus.Active, FullName = role.ToString(), Email = $"{id}@test.invalid", PasswordHash = "credential-must-not-leak" });
            await db.SaveChangesAsync();
            return true;
        });
        return factory;
    }
}