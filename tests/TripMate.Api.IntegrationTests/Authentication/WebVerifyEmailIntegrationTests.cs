using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class WebVerifyEmailIntegrationTests
{
    private sealed class FirebaseStub : IFirebaseAuthService
    {
        public FirebaseTokenValidationResult Evidence { get; set; } = new("uid", " WEB@example.com ", true, null, null, "password");
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(string idToken, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Evidence);
        }
    }

    [Fact]
    public async Task Post_SynchronizesVerificationWithoutCreatingSession()
    {
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => new FirebaseStub());
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = "web@example.com",
                FullName = "Web",
                Role = UserRole.Traveler,
                Status = AccountStatus.PendingEmailVerification,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fresh-firebase-token");
        var response = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").EnumerateObject().Should().ContainSingle();
        json.GetProperty("data").GetProperty("emailVerified").GetBoolean().Should().BeTrue();
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(AccountStatus.Active);
            user.EmailVerifiedAtUtc.Should().NotBeNull();
            user.LastLoginAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }
    [Theory]
    [InlineData(null)]
    [InlineData("Basic token")]
    [InlineData("Bearer ")]
    public async Task Post_WithoutUsableBearer_ReturnsProblemWithoutCallingFirebase(string? header)
    {
        var firebase = new FirebaseStub();
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();
        if (header is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        var response = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("AUTH_HEADER_MISSING");
        json.TryGetProperty("success", out _).Should().BeFalse();
        firebase.Calls.Should().Be(0);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Theory]
    [InlineData("invalid", 401, "MSG14")]
    [InlineData("unavailable", 503, "auth.verification_unavailable")]
    [InlineData("unverified", 403, "MSG_EMAIL_NOT_VERIFIED")]
    [InlineData("emailMissing", 400, "auth.verification_email_missing")]
    [InlineData("accountMissing", 400, "MSG_USER_NOT_FOUND")]
    public async Task Post_WithInvalidEvidenceOrInfrastructureFailure_RejectsWithoutMutation(string scenario, int status, string code)
    {
        var firebase = new FirebaseStub();
        if (scenario == "invalid") firebase.Failure = new InvalidOperationException("private SDK internals");
        if (scenario == "unavailable") firebase.Failure = new FirebaseUnavailableException("private infrastructure internals");
        if (scenario == "unverified") firebase.Evidence = firebase.Evidence with { EmailVerified = false };
        if (scenario == "emailMissing") firebase.Evidence = firebase.Evidence with { Email = " " };
        if (scenario == "accountMissing") firebase.Evidence = firebase.Evidence with { Email = "unknown@example.com" };
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = "web@example.com",
                Role = UserRole.Traveler,
                Status = AccountStatus.PendingEmailVerification,
                CreatedAtUtc = before,
                UpdatedAtUtc = before
            });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fresh-token");
        var response = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        ((int)response.StatusCode).Should().Be(status);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("private");
        using var json = JsonDocument.Parse(raw);
        json.RootElement.GetProperty("errorCode").GetString().Should().Be(code);
        json.RootElement.TryGetProperty("success", out _).Should().BeFalse();
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(AccountStatus.PendingEmailVerification);
            user.EmailVerifiedAtUtc.Should().BeNull();
            user.UpdatedAtUtc.Should().Be(before);
            user.LastLoginAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }

    [Theory]
    [InlineData(AccountStatus.Locked, "auth.account_locked")]
    [InlineData(AccountStatus.Inactive, "auth.account_inactive")]
    [InlineData((AccountStatus)99, "auth.account_inactive")]
    public async Task Post_WithRestrictedAccount_PreservesAccountAndSession(AccountStatus status, string code)
    {
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => new FirebaseStub());
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User { Email = "web@example.com", Role = UserRole.TourOperator, Status = status, UpdatedAtUtc = before };
            db.Users.Add(user);
            db.RefreshTokens.Add(new RefreshToken { User = user, TokenHash = "existing-session", CreatedAtUtc = before, ExpiresAtUtc = before.AddDays(7) });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fresh-token");
        var response = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be(code);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(status);
            user.EmailVerifiedAtUtc.Should().BeNull();
            user.UpdatedAtUtc.Should().Be(before);
            db.RefreshTokens.Should().ContainSingle(t => t.TokenHash == "existing-session");
            return true;
        });
    }

    [Theory]
    [InlineData(AccountStatus.PendingEmailVerification)]
    [InlineData(AccountStatus.Active)]
    [InlineData(AccountStatus.PendingApproval)]
    [InlineData(AccountStatus.Rejected)]
    public async Task Post_RepeatedSynchronization_DoesNotCreateSessionsOrNormalizeLegacy(AccountStatus status)
    {
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => new FirebaseStub());
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = "web@example.com",
                Role = UserRole.TourOperator,
                Status = status,
                CreatedAtUtc = before,
                UpdatedAtUtc = before,
                EmailVerifiedAtUtc = status == AccountStatus.PendingEmailVerification ? null : before
            });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fresh-token");
        var first = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var verifiedAt = await factory.WithDbContextAsync(async db => (await db.Users.SingleAsync()).EmailVerifiedAtUtc);
        var second = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.Contains("Set-Cookie").Should().BeFalse();
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(status == AccountStatus.PendingEmailVerification ? AccountStatus.Active : status);
            user.EmailVerifiedAtUtc.Should().Be(verifiedAt);
            user.LastLoginAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }
    [Fact]
    public async Task Post_LegacyMobileVerificationStillEstablishesOneSessionAfterWebSync()
    {
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => new FirebaseStub());
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = "web@example.com",
                Role = UserRole.Traveler,
                Status = AccountStatus.PendingEmailVerification,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fresh-token");
        var web = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        web.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().BeEmpty(); return Task.FromResult(true); });
        var mobile = await client.PostAsync("/api/v1/auth/verify-email", null);
        mobile.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await mobile.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("data").GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
        await factory.WithDbContextAsync(db => { db.RefreshTokens.Should().ContainSingle(); return Task.FromResult(true); });
    }
}