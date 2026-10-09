using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class OperatorEmailVerificationIntegrationTests
{
    private sealed class FirebaseStub(bool verified) : IFirebaseAuthService
    {
        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(
            string idToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FirebaseTokenValidationResult(
                "operator-uid", "operator@example.com", verified, SignInProvider: "password"));
    }

    private sealed class VerificationStatusStub(bool verified) : IEmailVerificationStatusService
    {
        public int Calls { get; private set; }

        public Task<bool> IsVerifiedAsync(string email, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(verified);
        }
    }

    private static TripMateApiFactory Factory(bool firebaseVerified, VerificationStatusStub status) =>
        new(
            firebaseServiceFactory: _ => new FirebaseStub(firebaseVerified),
            configureTestServices: services =>
            {
                services.RemoveAll<IEmailVerificationStatusService>();
                services.AddSingleton<IEmailVerificationStatusService>(status);
            });

    private static Task<bool> Seed(TripMateApiFactory factory, AccountStatus accountStatus = AccountStatus.PendingApproval) =>
        factory.WithDbContextAsync(async db =>
        {
            using var scope = factory.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var created = DateTimeOffset.UtcNow.AddDays(-1);
            var user = new User
            {
                Email = "operator@example.com",
                FullName = "Operator",
                Role = UserRole.TourOperator,
                Status = accountStatus,
                PasswordHash = hasher.Hash("CorrectPass1"),
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
            };
            db.OperatorProfiles.Add(new OperatorProfile
            {
                User = user,
                CompanyName = "Operator Co",
                TaxCode = "TAX-VERIFY",
                BusinessLicenseNo = "LIC-VERIFY",
                ApprovalStatus = OperatorApprovalStatus.PendingApproval,
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
            });
            await db.SaveChangesAsync();
            return true;
        });

    [Fact]
    public async Task WebVerifyEmail_OperatorRemainsPendingAndGetsNoSession()
    {
        var status = new VerificationStatusStub(true);
        using var factory = Factory(true, status);
        using var client = factory.CreateClient();
        await Seed(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "verified-token");

        var first = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.Contains("Set-Cookie").Should().BeFalse();
        var marker = await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(AccountStatus.PendingApproval);
            user.EmailVerifiedAtUtc.Should().NotBeNull();
            user.LastLoginAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return user.EmailVerifiedAtUtc;
        });

        (await client.PostAsync("/api/v1/auth/web/verify-email", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync()).EmailVerifiedAtUtc.Should().Be(marker);
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task WebVerifyEmail_UnverifiedEvidenceDoesNotSetMarker()
    {
        using var factory = Factory(false, new VerificationStatusStub(false));
        using var client = factory.CreateClient();
        await Seed(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "unverified-token");
        var response = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync()).EmailVerifiedAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }

    [Theory]
    [InlineData(AccountStatus.Locked)]
    [InlineData(AccountStatus.Inactive)]
    public async Task WebVerifyEmail_RestrictedOperatorDoesNotSetMarker(AccountStatus accountStatus)
    {
        using var factory = Factory(true, new VerificationStatusStub(true));
        using var client = factory.CreateClient();
        await Seed(factory, accountStatus);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "verified-token");
        var response = await client.PostAsync("/api/v1/auth/web/verify-email", null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(accountStatus);
            user.EmailVerifiedAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task PasswordSignIn_RecoversVerifiedPendingOperatorWithoutBrowserFirebaseSession()
    {
        var status = new VerificationStatusStub(true);
        using var factory = Factory(false, status);
        using var client = factory.CreateClient();
        await Seed(factory);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login",
            new { email = "operator@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("Active");
        data.GetProperty("applicationStatus").GetString().Should().Be("PendingApproval");
        status.Calls.Should().Be(1);
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.Status.Should().Be(AccountStatus.PendingApproval);
            user.EmailVerifiedAtUtc.Should().NotBeNull();
            db.RefreshTokens.Should().ContainSingle();
            return true;
        });

        var marker = await factory.WithDbContextAsync(async db =>
            (await db.Users.SingleAsync()).EmailVerifiedAtUtc);
        (await client.PostAsJsonAsync("/api/v1/auth/web/login",
            new { email = "operator@example.com", password = "CorrectPass1" })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        status.Calls.Should().Be(1);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync()).EmailVerifiedAtUtc.Should().Be(marker);
            return true;
        });
    }

    [Theory]
    [InlineData("WrongPass1", true)]
    [InlineData("CorrectPass1", false)]
    public async Task PasswordSignIn_WrongPasswordOrUnverifiedFirebaseCannotCreateSession(
        string password, bool firebaseVerified)
    {
        var status = new VerificationStatusStub(firebaseVerified);
        using var factory = Factory(false, status);
        using var client = factory.CreateClient();
        await Seed(factory);
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login",
            new { email = "operator@example.com", password });
        response.StatusCode.Should().Be(password == "WrongPass1"
            ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        status.Calls.Should().Be(password == "WrongPass1" ? 0 : 1);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync()).EmailVerifiedAtUtc.Should().BeNull();
            db.RefreshTokens.Should().BeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task PasswordSignIn_PendingOperatorWithoutApplicationDoesNotQueryFirebaseStatus()
    {
        var status = new VerificationStatusStub(true);
        using var factory = Factory(false, status);
        using var client = factory.CreateClient();
        await Seed(factory);
        await factory.WithDbContextAsync(async db =>
        {
            db.OperatorProfiles.Remove(await db.OperatorProfiles.SingleAsync());
            await db.SaveChangesAsync();
            return true;
        });
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login",
            new { email = "operator@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        status.Calls.Should().Be(0);
        await factory.WithDbContextAsync(db =>
        {
            db.RefreshTokens.Should().BeEmpty();
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task PasswordSignIn_InvalidPersistedMarkerRequiresFreshVerifiedEvidence()
    {
        var status = new VerificationStatusStub(true);
        using var factory = Factory(false, status);
        using var client = factory.CreateClient();
        await Seed(factory);
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.EmailVerifiedAtUtc = user.CreatedAtUtc.AddMinutes(-1);
            await db.SaveChangesAsync();
            return true;
        });
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login",
            new { email = "operator@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        status.Calls.Should().Be(1);
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.EmailVerifiedAtUtc.Should().BeOnOrAfter(user.CreatedAtUtc);
            return true;
        });
    }
}