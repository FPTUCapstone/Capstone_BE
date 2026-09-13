using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// End-to-end contract tests for `POST /api/v1/auth/google` (UC-04 spec v2.0 §7.3):
/// G1-A response shape, the ratified status matrix over HTTP, claim fail-closed gates
/// (BR-11/BR-12), the 503 Firebase-unavailable mapping (BR-16) and the ProblemDetails-only
/// error shape (two-shapes rule).
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public class GoogleSignInIntegrationTests
{
    private sealed class StubFirebaseService : IFirebaseAuthService
    {
        public FirebaseTokenValidationResult? ResultToReturn { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(
            string idToken,
            CancellationToken cancellationToken = default)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(ResultToReturn!);
        }
    }

    private static (TripMateApiFactory Factory, HttpClient Client) CreateGoogleFactory(
        StubFirebaseService firebase)
    {
        var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        var client = factory.CreateClient();
        return (factory, client);
    }

    private static FirebaseTokenValidationResult VerifiedGoogleIdentity(
        string email,
        bool emailVerified = true,
        string? signInProvider = "google.com") =>
        new("fb-uid-1", email, emailVerified, "Test User", null, signInProvider);

    [Fact]
    public async Task Post_Google_WithValidTokenAndUnknownEmail_ProvisionsTravelerAndReturnsG1AShape()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("new.traveler@gmail.com"),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = body.GetProperty("data");
        data.GetProperty("isNewAccount").GetBoolean().Should().BeTrue();
        data.GetProperty("email").GetString().Should().Be("new.traveler@gmail.com");
        data.GetProperty("role").GetString().Should().Be("Traveler");
        data.GetProperty("status").GetString().Should().Be("Active");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("accessTokenExpiresAtUtc").GetString().Should().NotBeNullOrWhiteSpace();

        var persisted = await factory.WithDbContextAsync(db =>
            db.Users.SingleAsync(u => u.Email == "new.traveler@gmail.com"));
        persisted.Role.Should().Be(UserRole.Traveler);
        persisted.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public async Task Post_Google_WithExistingLockedAccount_Returns403AccountLocked()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("locked.operator@example.com"),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new TripMate.Domain.Entities.User
            {
                Email = "locked.operator@example.com",
                FullName = "Locked Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Locked,
            });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("auth.account_locked");
        body.GetProperty("title").GetString().Should().Be("Account is locked.");
    }

    [Fact]
    public async Task Post_Google_WithPendingApprovalAccount_Returns403PendingApprovalCode()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("pending.operator@example.com"),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new TripMate.Domain.Entities.User
            {
                Email = "pending.operator@example.com",
                FullName = "Pending Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.PendingApproval,
            });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("auth.account_pending_approval");
        body.GetProperty("title").GetString().Should().Be("Account is pending approval.");
    }

    [Fact]
    public async Task Post_Google_WithPendingEmailVerification_Returns403Unverified()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("unverified@example.com"),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new TripMate.Domain.Entities.User
            {
                Email = "unverified@example.com",
                FullName = "Unverified Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.PendingEmailVerification,
            });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG_UNVERIFIED");
    }

    [Fact]
    public async Task Post_Google_WithRejectedOperator_Returns200AndKeepsRejectedStatus()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("rejected.operator@example.com"),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new TripMate.Domain.Entities.User
            {
                Email = "rejected.operator@example.com",
                FullName = "Rejected Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Rejected,
            });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("Rejected");
        data.GetProperty("role").GetString().Should().Be("TourOperator");
        data.GetProperty("isNewAccount").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Post_Google_WithNonGoogleProvider_Returns401TokenInvalid()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("user@example.com", signInProvider: "password"),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task Post_Google_WithUnverifiedEmailClaim_Returns403EmailNotVerified()
    {
        var firebase = new StubFirebaseService
        {
            ResultToReturn = VerifiedGoogleIdentity("user@example.com", emailVerified: false),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG_EMAIL_NOT_VERIFIED");
        body.GetProperty("title").GetString().Should().Be("Google account email has not been verified.");
    }

    [Fact]
    public async Task Post_Google_WhenFirebaseUnavailable_Returns503ServiceUnavailable()
    {
        var firebase = new StubFirebaseService
        {
            ExceptionToThrow = new FirebaseUnavailableException(
                "Firebase ID token verification is temporarily unavailable."),
        };
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid-token" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("auth.firebase_unavailable");
        body.GetProperty("title").GetString().Should().Be("Google authentication service is temporarily unavailable.");
    }

    [Fact]
    public async Task Post_Google_WithoutIdToken_ReturnsProblemDetails400WithErrorCode()
    {
        var firebase = new StubFirebaseService();
        var (factory, client) = CreateGoogleFactory(firebase);
        using var factoryOwner = factory;

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_MISSING");
        body.GetProperty("title").GetString().Should().Be("Google ID token is required.");

        // Two-shapes rule: failures are ProblemDetails, never the success envelope.
        body.TryGetProperty("success", out _).Should().BeFalse(
            "an error response must never carry the success-envelope shape");
    }
}