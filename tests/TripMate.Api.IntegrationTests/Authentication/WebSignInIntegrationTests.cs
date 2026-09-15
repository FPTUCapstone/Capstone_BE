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

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class WebSignInIntegrationTests
{
    private static Task<bool> Seed(TripMateApiFactory factory, UserRole role) => factory.WithDbContextAsync(async db =>
    {
        using var scope = factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        db.Users.Add(new User
        {
            Email = "login@example.com",
            FullName = "",
            Role = role,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash("CorrectPass1"),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_WebLogin_ReturnsContextAndCookieWithOriginalExpiry(bool keep)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Traveler);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email = " LOGIN@example.com ", password = "CorrectPass1", keepMeSignedIn = keep });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        data.GetProperty("applicationStatus").ValueKind.Should().Be(JsonValueKind.Null);
        data.TryGetProperty("refreshToken", out _).Should().BeFalse();
        data.GetProperty("status").GetString().Should().Be("Active");
        (data.GetProperty("accessTokenExpiresAtUtc").GetDateTimeOffset() - DateTimeOffset.UtcNow)
            .Should().BeCloseTo(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        cookie.Should().StartWith("tripmate_refresh=").And.Contain("httponly").And.Contain("secure").And.Contain("samesite=lax").And.Contain("path=/api/v1/auth");
        cookie.Should().NotContain("domain=");
        cookie.Contains("expires=").Should().Be(keep);
        await factory.WithDbContextAsync(async db =>
        {
            var token = await db.RefreshTokens.SingleAsync();
            (token.ExpiresAtUtc - token.CreatedAtUtc).Should().Be(TimeSpan.FromDays(7));
            var raw = Uri.UnescapeDataString(cookie.Split(';')[0].Split('=', 2)[1]);
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IJwtTokenService>().HashRefreshToken(raw).Should().Be(token.TokenHash);
            if (keep)
            {
                var expires = cookie.Split(';').Single(part => part.TrimStart().StartsWith("expires="));
                var boundary = DateTimeOffset.Parse(expires.Trim()["expires=".Length..], System.Globalization.CultureInfo.InvariantCulture);
                (token.ExpiresAtUtc - boundary).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
            }
            return true;
        });
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Post_AdminLogin_NonAdminDeniedBeforeSession(UserRole role)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, role);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/admin/login", new { email = "login@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("auth.admin_access_required");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(async db =>
        {
            db.RefreshTokens.Should().BeEmpty();
            (await db.Users.SingleAsync()).LastLoginAtUtc.Should().BeNull();
            return true;
        });
    }
    [Fact]
    public async Task Post_AdminLogin_AdministratorCreatesSession()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Administrator);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/admin/login", new { email = "login@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("role").GetString().Should().Be("Administrator");
        response.Headers.GetValues("Set-Cookie").Should().ContainSingle();
    }

    [Fact]
    public async Task Post_FailedReplacementPreservesExistingSession()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Traveler);
        var first = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email = "login@example.com", password = "CorrectPass1" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var original = await factory.WithDbContextAsync(async db => (await db.RefreshTokens.SingleAsync()).TokenHash);
        var failed = await client.PostAsJsonAsync("/api/v1/auth/web/admin/login", new { email = "login@example.com", password = "CorrectPass1", role = "Administrator", administratorOnly = false });
        failed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        failed.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().ContainSingle(t => t.TokenHash == original); return Task.FromResult(true); });
    }

    [Theory]
    [InlineData("{\"email\":\"login@example.com\",\"password\":\"CorrectPass1\",\"keepMeSignedIn\":null}")]
    [InlineData("{\"email\":\"login@example.com\",\"password\":\"CorrectPass1\",\"keepMeSignedIn\":\"yes\"}")]
    [InlineData("{")]
    public async Task Post_InvalidBinding_ReturnsStableWebCodeWithoutSession(string body)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/auth/web/login", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("auth.request_invalid");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Theory]
    [InlineData("", "CorrectPass1", "email", "MSG01")]
    [InlineData("bad-email", "CorrectPass1", "email", "MSG02")]
    [InlineData("login@example.com", "", "password", "MSG01")]
    public async Task Post_InvalidFields_ProvidesFieldCodes(string email, string password, string field, string code)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errors").GetProperty(field)[0].GetString().Should().Be(code);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval, "PendingApproval")]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected, "Rejected")]
    [InlineData(AccountStatus.Active, OperatorApprovalStatus.Approved, "Approved")]
    [InlineData(AccountStatus.Active, null, null)]
    public async Task Post_OperatorContext_UsesCurrentProfileAndEffectiveStatus(AccountStatus status, OperatorApprovalStatus? approval, string? expected)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.TourOperator);
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status = status;
            user.EmailVerifiedAtUtc = user.CreatedAtUtc;
            if (approval.HasValue) db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval.Value });
            await db.SaveChangesAsync();
            return true;
        });
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email = "login@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("status").GetString().Should().Be("Active");
        json.GetProperty("data").GetProperty("applicationStatus").GetString().Should().Be(expected);
        await factory.WithDbContextAsync(async db => { (await db.Users.SingleAsync()).Status.Should().Be(status); return true; });
    }

    private sealed class FirebaseStub : IFirebaseAuthService
    {
        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(string idToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FirebaseTokenValidationResult("uid", "login@example.com", true, "Name", null, "google.com"));
    }

    [Theory]
    [InlineData(UserRole.Traveler, false)]
    [InlineData(UserRole.Administrator, true)]
    public async Task Post_WebGoogle_UsesCookieContractAndRetainsAdminGate(UserRole role, bool denied)
    {
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => new FirebaseStub());
        using var client = factory.CreateClient();
        await Seed(factory, role);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/google", new { idToken = "firebase-token", keepMeSignedIn = true });
        response.StatusCode.Should().Be(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (denied)
        {
            json.GetProperty("errorCode").GetString().Should().Be("auth.admin_google_sign_in_disabled");
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
            await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().BeEmpty(); return Task.FromResult(true); });
        }
        else
        {
            var data = json.GetProperty("data");
            data.GetProperty("applicationStatus").ValueKind.Should().Be(JsonValueKind.Null);
            data.TryGetProperty("refreshToken", out _).Should().BeFalse();
            data.GetProperty("isNewAccount").GetBoolean().Should().BeFalse();
            response.Headers.GetValues("Set-Cookie").Single().Should().Contain("tripmate_refresh=").And.Contain("expires=");
        }
    }
    [Theory]
    [InlineData(AccountStatus.Locked, "auth.account_locked")]
    [InlineData(AccountStatus.Inactive, "auth.account_inactive")]
    [InlineData(AccountStatus.PendingEmailVerification, "MSG_UNVERIFIED")]
    [InlineData(AccountStatus.PendingApproval, "auth.account_state_unresolved")]
    public async Task Post_AdminEntry_AccountGatePrecedesRoleGate(AccountStatus status, string code)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Traveler);
        await factory.WithDbContextAsync(async db => { (await db.Users.SingleAsync()).Status = status; await db.SaveChangesAsync(); return true; });
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/admin/login", new { email = "login@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be(code);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().BeEmpty(); return Task.FromResult(true); });
    }

    [Fact]
    public async Task Post_WrongCredentialsAndWrongContentType_DoNotCreateCookie()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Traveler);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email = "login@example.com", password = "WrongPass" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("auth.invalid_credentials");
        using var plain = new StringContent("{}");
        var wrongType = await client.PostAsync("/api/v1/auth/web/login", plain);
        wrongType.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().BeEmpty(); return Task.FromResult(true); });
    }

    [Theory]
    [InlineData("http://localhost:3000", true)]
    [InlineData("https://unapproved.example", false)]
    public async Task Preflight_UsesExactOriginAndCredentials(string origin, bool allowed)
    {
        using var factory = new TripMateApiFactory(corsAllowedOrigins: ["http://localhost:3000"]);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/web/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        var response = await client.SendAsync(request);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().Be(allowed);
        if (allowed)
        {
            response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(origin);
            response.Headers.GetValues("Access-Control-Allow-Credentials").Single().Should().Be("true");
        }
    }
    [Fact]
    public async Task Post_WebGoogle_NewIdentityCreatesTravelerWithCookie()
    {
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => new FirebaseStub());
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/google", new { idToken = "firebase-token" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        data.GetProperty("role").GetString().Should().Be("Traveler");
        data.GetProperty("isNewAccount").GetBoolean().Should().BeTrue();
        data.GetProperty("applicationStatus").ValueKind.Should().Be(JsonValueKind.Null);
        var id = data.GetProperty("userId").GetInt64();
        id.Should().BeGreaterThan(0);
        await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().ContainSingle(t => t.UserId == id); return Task.FromResult(true); });
    }

    [Theory]
    [InlineData("Development", "http://localhost", false)]
    [InlineData("Development", "https://localhost", true)]
    [InlineData("Production", "http://localhost", true)]
    public async Task Post_SecureCookieMatchesApprovedEnvironment(string environment, string address, bool secure)
    {
        using var factory = new TripMateApiFactory(environmentName: environment);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri(address) });
        await Seed(factory, UserRole.Traveler);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email = "login@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Single().Contains("secure").Should().Be(secure);
    }
}