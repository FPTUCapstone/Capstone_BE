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
/// UC-07 Change Password API adapters: POST /api/v1/auth/change-password (Mobile/shared) and
/// POST /api/v1/auth/web/change-password (Web, HttpOnly cookie). See
/// specs/UC-07-change-password-spec.md §5 for the error-code contract.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ChangePasswordEndpointsTests
{
    private const string MobileRoute = "/api/v1/auth/change-password";
    private const string WebRoute = "/api/v1/auth/web/change-password";

    [Fact]
    public async Task Mobile_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mobile_WithMissingFields_ReturnsBadRequestMsg01()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "", newPassword = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG01");
    }

    [Fact]
    public async Task Mobile_WithWrongCurrentPassword_ReturnsBadRequestMsg17()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "WrongPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG17");
    }

    [Fact]
    public async Task Mobile_WithWeakNewPassword_ReturnsBadRequestMsg05()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "weak" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG05");
    }

    [Fact]
    public async Task Mobile_WithNewPasswordEqualToCurrent_ReturnsBadRequestMsg05()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "CorrectPass1!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG05");
    }

    [Fact]
    public async Task Mobile_WithNoLocalPassword_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, password: null);
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "Anything1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mobile_WithValidRequest_ReplacesSessionAndRevokesPriorTokens()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        await SeedRefreshTokenAsync(factory, user.Id);
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();

        await factory.WithDbContextAsync(async db =>
        {
            var tokens = await db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync();
            tokens.Should().HaveCount(2);
            tokens.Count(t => t.RevokedAtUtc == null).Should().Be(1);

            using var scope = factory.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var updated = await db.Users.SingleAsync(u => u.Id == user.Id);
            hasher.Verify("NewPass2@", updated.PasswordHash!).Should().BeTrue();
            hasher.Verify("CorrectPass1!", updated.PasswordHash!).Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task Web_WithValidRequest_SetsCookieAndOmitsRefreshTokenFromJson()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            WebRoute, new { currentPassword = "CorrectPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(cookie => cookie.StartsWith("tripmate_refresh=", StringComparison.Ordinal));

        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.TryGetProperty("refreshToken", out _).Should().BeFalse();
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Web_WithWrongCurrentPassword_PreservesExistingSession()
    {
        await using var factory = new TripMateApiFactory();
        var user = await SeedUserAsync(factory, "CorrectPass1!");
        await SeedRefreshTokenAsync(factory, user.Id);
        using var client = factory.CreateAuthenticatedClient(user.Id, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            WebRoute, new { currentPassword = "WrongPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
                .Should().ContainSingle(t => t.RevokedAtUtc == null);
            return true;
        });
    }

    private static async Task<User> SeedUserAsync(TripMateApiFactory factory, string? password)
    {
        return await factory.WithDbContextAsync(async db =>
        {
            using var scope = factory.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var user = new User
            {
                Email = $"uc07-{Guid.NewGuid():N}@example.com",
                FullName = "UC-07 Test User",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                PasswordHash = password is null ? null : hasher.Hash(password),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        });
    }

    private static async Task SeedRefreshTokenAsync(TripMateApiFactory factory, long userId)
    {
        await factory.WithDbContextAsync(async db =>
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId,
                TokenHash = $"hashed:seed-{Guid.NewGuid()}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            });
            await db.SaveChangesAsync();
            return true;
        });
    }
}