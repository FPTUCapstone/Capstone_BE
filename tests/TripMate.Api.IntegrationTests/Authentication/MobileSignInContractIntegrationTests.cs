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

/// <summary>
/// A1 — Mobile/shared auth wire contract (spec §7): applicationStatus must be present on
/// POST /api/v1/auth/login and /api/v1/auth/google for the Mobile client, with the same
/// backend-authoritative semantics the Web contract uses. Traveler -> JSON null (present, not
/// omitted); recognized TourOperator -> Approved/PendingApproval/Rejected.
/// Google Administrator stays a 403 (never a successful session). Web endpoints are separate
/// types and are asserted unchanged elsewhere; these tests prove the legacy wire only.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public class MobileSignInContractIntegrationTests
{
    private static async Task SeedLoginUser(
        TripMateApiFactory factory, UserRole role, OperatorApprovalStatus? approval)
    {
        await factory.WithDbContextAsync(async db =>
        {
            using var scope = factory.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var user = new User
            {
                Email = "mobile@example.com",
                FullName = "Mobile User",
                Role = role,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash("CorrectPass1"),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            if (approval.HasValue)
            {
                db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval.Value });
                await db.SaveChangesAsync();
            }
            return true;
        });
    }

    [Fact]
    public async Task Post_LegacyLogin_ActiveAdministrator_IsForbiddenWithoutSessionSideEffects()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await SeedLoginUser(factory, UserRole.Administrator, null);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email = "mobile@example.com", password = "CorrectPass1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("auth.admin_mobile_sign_in_disabled");
        body.GetProperty("title").GetString().Should().Be("Administrator accounts are supported on Web only.");
        body.ToString().Should().NotContain("accessToken").And.NotContain("refreshToken");
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == "mobile@example.com");
            user.LastLoginAtUtc.Should().BeNull();
            (await db.RefreshTokens.CountAsync(t => t.UserId == user.Id)).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Post_LegacyLogin_AdministratorWrongPassword_RemainsGenericInvalidCredentials()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await SeedLoginUser(factory, UserRole.Administrator, null);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email = "mobile@example.com", password = "WrongPass1" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("auth.invalid_credentials");
        body.ToString().ToLowerInvariant().Should().NotContain("administrator");
    }

    [Theory]
    [InlineData(UserRole.Traveler, null, null)]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Approved, "Approved")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.PendingApproval, "PendingApproval")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Rejected, "Rejected")]
    public async Task Post_LegacyLogin_ExposesApplicationStatusOnMobileWire(
        UserRole role, OperatorApprovalStatus? approval, string? expected)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await SeedLoginUser(factory, role, approval);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email = "mobile@example.com", password = "CorrectPass1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        data.TryGetProperty("applicationStatus", out var app).Should().BeTrue(
            "applicationStatus must be present on the Mobile login wire, not omitted");
        if (expected is null)
        {
            app.ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            app.GetString().Should().Be(expected);
        }
        // Existing Mobile token contract is preserved.
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private sealed class StubFirebase : IFirebaseAuthService
    {
        public required FirebaseTokenValidationResult Result { get; init; }
        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(string idToken, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    [Fact]
    public async Task Post_LegacyGoogle_FirstTimeTraveler_ExposesNullApplicationStatus()
    {
        var firebase = new StubFirebase
        {
            Result = new FirebaseTokenValidationResult("uid", "new.traveler@gmail.com", true, "New Traveler", null, "google.com"),
        };
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("role").GetString().Should().Be("Traveler");
        data.TryGetProperty("applicationStatus", out var app).Should().BeTrue();
        app.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(OperatorApprovalStatus.Approved, "Approved")]
    [InlineData(OperatorApprovalStatus.PendingApproval, "PendingApproval")]
    [InlineData(OperatorApprovalStatus.Rejected, "Rejected")]
    public async Task Post_LegacyGoogle_ExistingTourOperator_ExposesCurrentApplicationStatus(
        OperatorApprovalStatus approval, string expected)
    {
        var firebase = new StubFirebase
        {
            Result = new FirebaseTokenValidationResult("uid", "op@example.com", true, "Op", null, "google.com"),
        };
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Email = "op@example.com",
                FullName = "Op",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.TryGetProperty("applicationStatus", out var app).Should().BeTrue();
        app.GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Post_LegacyGoogle_Administrator_RemainsForbidden()
    {
        var firebase = new StubFirebase
        {
            Result = new FirebaseTokenValidationResult("uid", "admin@example.com", true, "Admin", null, "google.com"),
        };
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User { Email = "admin@example.com", FullName = "Admin", Role = UserRole.Administrator, Status = AccountStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
            return true;
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "valid" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("auth.admin_google_sign_in_disabled");
    }

    [Fact]
    public async Task Post_LegacyVerifyEmail_ReturnsFullRoutingIdentity()
    {
        var firebase = new StubFirebase
        {
            Result = new FirebaseTokenValidationResult(
                "uid", "verify@example.com", true, "Verify User", null, "password"),
        };
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = "verify@example.com",
                FullName = "Verify User",
                Role = UserRole.Traveler,
                Status = AccountStatus.PendingEmailVerification,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fresh-token");

        var response = await client.PostAsync("/api/v1/auth/verify-email", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("role").GetString().Should().Be("Traveler");
        data.GetProperty("status").GetString().Should().Be("Active");
        data.TryGetProperty("applicationStatus", out var app).Should().BeTrue();
        app.ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Post_LegacyVerifyEmail_TourOperatorReturnsCurrentApplicationStatus()
    {
        var firebase = new StubFirebase
        {
            Result = new FirebaseTokenValidationResult(
                "uid", "verify-operator@example.com", true, "Verify Operator", null, "password"),
        };
        using var factory = new TripMateApiFactory(firebaseServiceFactory: _ => firebase);
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Email = "verify-operator@example.com",
                FullName = "Verify Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.PendingEmailVerification,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = user.Id,
                ApprovalStatus = OperatorApprovalStatus.PendingApproval,
            });
            await db.SaveChangesAsync();
            return true;
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fresh-token");

        var response = await client.PostAsync("/api/v1/auth/verify-email", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("role").GetString().Should().Be("TourOperator");
        data.GetProperty("status").GetString().Should().Be("Active");
        data.GetProperty("applicationStatus").GetString().Should().Be("PendingApproval");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
    }
}