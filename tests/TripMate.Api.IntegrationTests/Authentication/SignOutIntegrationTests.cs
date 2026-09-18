using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class SignOutIntegrationTests
{
    private const string LogRelativePath = "logs/uc04-s11.log";

    private static string LogFullPath =>
        Path.Combine(Directory.GetCurrentDirectory(), LogRelativePath);

    [Fact]
    public async Task Post_MobileLogout_TravelerActiveSession_RevokesSession()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "mobile-traveler-refresh";
        var seeded = await SeedSession(factory, UserRole.Traveler, rawToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = rawToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("message").GetString().Should().Be("Signed out successfully.");
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().NotBeNull();
            return true;
        });
    }

    [Fact]
    public async Task Post_WebLogout_TravelerActiveSession_RevokesSessionAndDeletesCookie()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "web-traveler-refresh";
        var seeded = await SeedSession(factory, UserRole.Traveler, rawToken);
        using var request = WebLogoutRequest(rawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletionCookie(response);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().NotBeNull();
            return true;
        });
    }

    [Fact]
    public async Task Post_WebLogout_NoCookie_ReturnsSuccessAndDeletesCookie()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedSession(factory, UserRole.Traveler, "unrelated-session");

        var response = await client.PostAsync("/api/v1/auth/web/logout", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletionCookie(response);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().BeNull();
            return true;
        });
    }

    [Fact]
    public async Task Post_MobileLogout_TourOperator_PreservesUserAndProfileState()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "operator-refresh";
        var seeded = await SeedSession(
            factory,
            UserRole.TourOperator,
            rawToken,
            OperatorApprovalStatus.Approved);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = rawToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync(item => item.Id == seeded.UserId);
            var profile = await db.OperatorProfiles.SingleAsync(item => item.UserId == seeded.UserId);
            user.Role.Should().Be(UserRole.TourOperator);
            user.Status.Should().Be(AccountStatus.Active);
            user.LastLoginAtUtc.Should().Be(seeded.LastLoginAtUtc);
            user.UpdatedAtUtc.Should().Be(seeded.UserUpdatedAtUtc);
            user.PasswordHash.Should().Be(seeded.PasswordHash);
            profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
            profile.ReviewedAtUtc.Should().Be(seeded.ProfileReviewedAtUtc);
            profile.UpdatedAtUtc.Should().Be(seeded.ProfileUpdatedAtUtc);
            return true;
        });
    }

    [Fact]
    public async Task Post_WebLogout_Administrator_RevokesSessionAndDeletesCookie()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "administrator-refresh";
        var seeded = await SeedSession(factory, UserRole.Administrator, rawToken);
        using var request = WebLogoutRequest(rawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletionCookie(response);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().NotBeNull();
            return true;
        });
    }

    [Fact]
    public async Task Post_WebLogout_Traveler_RendersRefreshTokenInvalid()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "traveler-refresh-to-reject";
        await SeedSession(factory, UserRole.Traveler, rawToken);
        using var logoutRequest = WebLogoutRequest(rawToken);

        var logoutResponse = await client.SendAsync(logoutRequest);
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        refreshRequest.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(rawToken)}");
        var refreshResponse = await client.SendAsync(refreshRequest);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertDeletionCookie(logoutResponse);
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await refreshResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task Post_MobileLogout_AlreadyRevokedToken_PreservesOriginalTimestamp()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "already-revoked-refresh";
        var originalTimestamp = DateTimeOffset.UtcNow.AddDays(-2);
        var seeded = await SeedSession(
            factory,
            UserRole.Traveler,
            rawToken,
            revokedAtUtc: originalTimestamp);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = rawToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().Be(originalTimestamp);
            return true;
        });
    }

    [Fact]
    public async Task Post_Logout_DeviceA_LeavesDeviceBSessionActiveAndRefreshable()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var deviceA = "device-a-refresh";
        var deviceB = "device-b-refresh";
        var seeded = await SeedSession(factory, UserRole.Traveler, deviceA);
        var deviceBSessionId = await AddSession(factory, seeded.UserId, deviceB);

        var logoutResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = deviceA });
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        refreshRequest.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(deviceB)}");
        var refreshResponse = await client.SendAsync(refreshRequest);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().NotBeNull();
            (await db.RefreshTokens.SingleAsync(token => token.Id == deviceBSessionId))
                .RevokedAtUtc.Should().BeNull();
            return true;
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_MobileLogout_NullOrWhitespaceToken_ReturnsSuccessWithoutMutation(
        string? rawToken)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var seeded = await SeedSession(factory, UserRole.Traveler, "untouched-refresh");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = rawToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId))
                .RevokedAtUtc.Should().BeNull();
            return true;
        });
    }

    [Fact]
    public async Task Post_Logout_Repeated_IdempotentlyPreservesRevokedTimestamp()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var rawToken = "repeated-logout-refresh";
        var seeded = await SeedSession(factory, UserRole.Traveler, rawToken);

        var first = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = rawToken });
        var firstTimestamp = await factory.WithDbContextAsync(async db =>
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId)).RevokedAtUtc);
        await Task.Delay(20);
        var second = await client.PostAsJsonAsync(
            "/api/v1/auth/logout", new { refreshToken = rawToken });
        var secondTimestamp = await factory.WithDbContextAsync(async db =>
            (await db.RefreshTokens.SingleAsync(token => token.Id == seeded.SessionId)).RevokedAtUtc);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        firstTimestamp.Should().NotBeNull();
        secondTimestamp.Should().Be(firstTimestamp);
    }

    [Fact]
    public async Task Post_WebLogout_SaveChangesThrows_Returns500ClearsCookieAndDoesNotLogSecrets()
    {
        ResetLogFile();
        var interceptor = new ToggleSaveChangesFailureInterceptor();
        await using var factory = new TripMateApiFactory(saveChangesInterceptor: interceptor);
        using var client = factory.CreateClient();
        const string rawToken = "tc14/raw+refresh=secret";
        const string accessToken = "tc14.access.jwt.secret";
        const string password = "tc14-password-secret";
        const string passwordHash = "tc14-password-hash-secret";
        var encodedToken = Uri.EscapeDataString(rawToken);
        var cookieHeader = $"tripmate_refresh={encodedToken}";
        await SeedSession(
            factory,
            UserRole.Traveler,
            rawToken,
            passwordHash: passwordHash);
        interceptor.FaultCount.Should().Be(0);
        interceptor.ArmNextSaveFailure();
        using var request = WebLogoutRequest(rawToken);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Test-Password", password);
        request.Headers.Add("X-Test-Password-Hash", passwordHash);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(500);
        problem.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        AssertDeletionCookie(response);
        interceptor.FaultCount.Should().Be(1);

        var logged = await ReadLogUntil(
            text => text.Contains("/api/v1/auth/web/logout", StringComparison.Ordinal)
                && text.Contains("500", StringComparison.Ordinal));
        logged.Should().Contain("/api/v1/auth/web/logout");
        logged.Should().Contain("500");
        logged.Should().NotContain(rawToken);
        logged.Should().NotContain(encodedToken);
        logged.Should().NotContain(cookieHeader);
        logged.Should().NotContain(accessToken);
        logged.Should().NotContain($"Bearer {accessToken}");
        logged.Should().NotContain(password);
        logged.Should().NotContain(passwordHash);
    }

    private static HttpRequestMessage WebLogoutRequest(string rawToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/logout");
        request.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(rawToken)}");
        return request;
    }

    private static void AssertDeletionCookie(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("tripmate_refresh=", StringComparison.OrdinalIgnoreCase));
        var normalized = cookie.ToLowerInvariant();
        normalized.Should().Contain("tripmate_refresh=");
        normalized.Should().Contain("path=/api/v1/auth");
        normalized.Should().Contain("samesite=lax");
        normalized.Should().Contain("httponly");
        normalized.Should().Contain("secure");
        normalized.Should().Contain("max-age=0");
        normalized.Should().Contain("expires=thu, 01 jan 1970 00:00:00 gmt");
    }

    private static async Task<SeededSession> SeedSession(
        TripMateApiFactory factory,
        UserRole role,
        string rawToken,
        OperatorApprovalStatus? approvalStatus = null,
        DateTimeOffset? revokedAtUtc = null,
        string passwordHash = "hashed:password")
    {
        using var scope = factory.Services.CreateScope();
        var jwtTokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();

        return await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Sign Out User",
                Role = role,
                Status = AccountStatus.Active,
                PasswordHash = passwordHash,
                CreatedAtUtc = now.AddDays(-10),
                UpdatedAtUtc = now.AddDays(-1),
                LastLoginAtUtc = now.AddHours(-1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            DateTimeOffset? profileReviewedAtUtc = null;
            DateTimeOffset? profileUpdatedAtUtc = null;
            if (approvalStatus.HasValue)
            {
                profileReviewedAtUtc = now.AddDays(-2);
                profileUpdatedAtUtc = now.AddDays(-2);
                db.OperatorProfiles.Add(new OperatorProfile
                {
                    UserId = user.Id,
                    CompanyName = "TripMate Tours",
                    TaxCode = "TAX-001",
                    BusinessLicenseNo = "BL-001",
                    ApprovalStatus = approvalStatus.Value,
                    ReviewedAtUtc = profileReviewedAtUtc,
                    CreatedAtUtc = now.AddDays(-8),
                    UpdatedAtUtc = profileUpdatedAtUtc.Value,
                });
                await db.SaveChangesAsync();
            }

            var session = new RefreshToken
            {
                UserId = user.Id,
                TokenHash = jwtTokenService.HashRefreshToken(rawToken),
                CreatedAtUtc = now.AddHours(-1),
                ExpiresAtUtc = now.AddDays(7),
                RevokedAtUtc = revokedAtUtc,
            };
            db.RefreshTokens.Add(session);
            await db.SaveChangesAsync();
            return new SeededSession(
                user.Id,
                session.Id,
                user.LastLoginAtUtc,
                user.UpdatedAtUtc,
                user.PasswordHash,
                profileReviewedAtUtc,
                profileUpdatedAtUtc);
        });
    }

    private static async Task<long> AddSession(
        TripMateApiFactory factory,
        long userId,
        string rawToken)
    {
        using var scope = factory.Services.CreateScope();
        var jwtTokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await factory.WithDbContextAsync(async db =>
        {
            var session = new RefreshToken
            {
                UserId = userId,
                TokenHash = jwtTokenService.HashRefreshToken(rawToken),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            };
            db.RefreshTokens.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });
    }

    private static void ResetLogFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFullPath)!);
        if (File.Exists(LogFullPath))
        {
            File.Delete(LogFullPath);
        }
    }

    private static async Task<string> ReadLogUntil(Func<string, bool> completed)
    {
        var logged = string.Empty;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(300);
            if (!File.Exists(LogFullPath))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(
                    LogFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                logged = await reader.ReadToEndAsync();
            }
            catch (IOException)
            {
                continue;
            }

            if (completed(logged))
            {
                break;
            }
        }

        return logged;
    }

    private sealed record SeededSession(
        long UserId,
        long SessionId,
        DateTimeOffset? LastLoginAtUtc,
        DateTimeOffset UserUpdatedAtUtc,
        string? PasswordHash,
        DateTimeOffset? ProfileReviewedAtUtc,
        DateTimeOffset? ProfileUpdatedAtUtc);

    private sealed class ToggleSaveChangesFailureInterceptor : SaveChangesInterceptor
    {
        private int _throwNextSave;
        private int _faultCount;

        public int FaultCount => Volatile.Read(ref _faultCount);

        public void ArmNextSaveFailure() =>
            Interlocked.Exchange(ref _throwNextSave, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _throwNextSave, 0) == 1)
            {
                Interlocked.Increment(ref _faultCount);
                throw new InvalidOperationException("Simulated infrastructure failure for TC-14");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}