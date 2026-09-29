using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class SignOutEndpointsTests
{
    [Fact]
    public async Task WebLogout_MissingCookie_IsIdempotentAndDeletesCookie()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/web/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletesRefreshCookie(response);
    }

    [Fact]
    public async Task WebLogout_ActiveSession_RevokesAuditsAndDeletesCookie()
    {
        await using var factory = new TripMateApiFactory();
        const string rawToken = "active-web-refresh";
        await SeedSessionsAsync(factory, rawToken, includeSecondUser: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"tripmate_refresh={rawToken}");

        var response = await client.PostAsync("/api/v1/auth/web/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletesRefreshCookie(response);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.CountAsync(
                token => token.RevokedAtUtc != null)).Should().Be(1);
            (await db.RefreshTokens.CountAsync(
                token => token.RevokedAtUtc == null)).Should().Be(1);
            var audit = await db.AuditLogs.SingleAsync();
            audit.ActionType.Should().Be(AuditActionTypes.AuthSignOut);
            audit.AfterData.Should().NotContain(rawToken);
            return true;
        });

        var refreshResponse = await client.PostAsync("/api/v1/auth/web/refresh", null);
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebLogout_UnknownSession_IsIdempotentAndDeletesCookie()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "tripmate_refresh=unknown-refresh");

        var response = await client.PostAsync("/api/v1/auth/web/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletesRefreshCookie(response);
    }

    [Fact]
    public async Task WebLogout_AlreadyRevokedSession_PreservesTimestampAndDeletesCookie()
    {
        await using var factory = new TripMateApiFactory();
        const string rawToken = "already-revoked-refresh";
        await SeedSessionsAsync(factory, rawToken, includeSecondUser: false);
        var firstRevokedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var scope = factory.Services.CreateScope();
        var tokenHash = scope.ServiceProvider
            .GetRequiredService<IJwtTokenService>()
            .HashRefreshToken(rawToken);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.TokenHash == tokenHash)).RevokedAtUtc = firstRevokedAt;
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"tripmate_refresh={rawToken}");

        var response = await client.PostAsync("/api/v1/auth/web/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletesRefreshCookie(response);
        await factory.WithDbContextAsync(async db =>
        {
            var session = await db.RefreshTokens.SingleAsync(token => token.TokenHash == tokenHash);
            session.RevokedAtUtc.Should().Be(firstRevokedAt);
            return true;
        });
    }

    [Fact]
    public async Task WebLogout_WhenPersistenceFails_PreservesCredentialForSuccessfulRetry()
    {
        var failureInterceptor = new FailNextSaveChangesInterceptor();
        await using var factory = new TripMateApiFactory(dbInterceptor: failureInterceptor);
        const string rawToken = "retryable-refresh";
        await SeedSessionsAsync(factory, rawToken, includeSecondUser: false);
        var tokenHash = Hash(factory, rawToken);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"tripmate_refresh={rawToken}");
        failureInterceptor.FailNextSave();

        var failedResponse = await client.PostAsync("/api/v1/auth/web/logout", null);

        failedResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failedResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        failedResponse.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.TokenHash == tokenHash))
                .RevokedAtUtc.Should().BeNull();
            return true;
        });

        var retryResponse = await client.PostAsync("/api/v1/auth/web/logout", null);

        retryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletesRefreshCookie(retryResponse);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.TokenHash == tokenHash))
                .RevokedAtUtc.Should().NotBeNull();
            return true;
        });
    }

    [Fact]
    public async Task MobileLogout_RevokedCredentialIsRejectedByWebRefresh()
    {
        await using var factory = new TripMateApiFactory();
        const string rawToken = "mobile-session-used-by-web";
        await SeedSessionsAsync(factory, rawToken, includeSecondUser: false);
        using var mobileClient = factory.CreateClient();

        var logoutResponse = await mobileClient.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = rawToken });

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        logoutResponse.Headers.Contains("Set-Cookie").Should().BeFalse();

        using var webClient = factory.CreateClient();
        webClient.DefaultRequestHeaders.Add("Cookie", $"tripmate_refresh={rawToken}");
        var refreshResponse = await webClient.PostAsync("/api/v1/auth/web/refresh", null);

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.AuditLogs.SingleAsync()).ActionType.Should().Be(AuditActionTypes.AuthSignOut);
            return true;
        });
    }

    private static void AssertDeletesRefreshCookie(HttpResponseMessage response)
    {
        response.Headers.GetValues("Set-Cookie").Should().ContainSingle();
        response.Headers.GetValues("Set-Cookie").Single()
            .Should().StartWith("tripmate_refresh=")
            .And.Contain("expires=")
            .And.Contain("path=/api/v1/auth");
    }

    private static async Task<(long CurrentUserId, long OtherUserId)> SeedSessionsAsync(
        TripMateApiFactory factory,
        string currentRawToken,
        bool includeSecondUser)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var currentUser = NewUser("current@example.com", now);
            db.Users.Add(currentUser);
            await db.SaveChangesAsync();
            db.RefreshTokens.AddRange(
                NewSession(currentUser.Id, currentRawToken, jwt, now),
                NewSession(currentUser.Id, "second-current-session", jwt, now));

            long otherUserId = 0;
            if (includeSecondUser)
            {
                var otherUser = NewUser("other@example.com", now);
                db.Users.Add(otherUser);
                await db.SaveChangesAsync();
                otherUserId = otherUser.Id;
                db.RefreshTokens.Add(NewSession(otherUser.Id, "other-user-session", jwt, now));
            }

            await db.SaveChangesAsync();
            return (currentUser.Id, otherUserId);
        });
    }

    private static User NewUser(string email, DateTimeOffset now) => new()
    {
        Email = email,
        FullName = "Test User",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        PasswordHash = "hashed:x",
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static RefreshToken NewSession(
        long userId,
        string rawToken,
        IJwtTokenService jwt,
        DateTimeOffset now) => new()
        {
            UserId = userId,
            TokenHash = jwt.HashRefreshToken(rawToken),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
        };

    private static string Hash(TripMateApiFactory factory, string rawToken)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().HashRefreshToken(rawToken);
    }

    private sealed class FailNextSaveChangesInterceptor : SaveChangesInterceptor
    {
        private int _failNextSave;

        public void FailNextSave() => Interlocked.Exchange(ref _failNextSave, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNextSave, 0) == 1)
            {
                throw new DbUpdateException("simulated persistence failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

}