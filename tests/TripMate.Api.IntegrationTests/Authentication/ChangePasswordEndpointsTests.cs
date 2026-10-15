using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// UC-07 Change Password API adapters: POST /api/v1/auth/change-password (Mobile/shared) and
/// POST /api/v1/auth/web/change-password (Web, HttpOnly cookie). See
/// specs/UC-07-change-password-spec.md §5 for the error-code contract.
///
/// Every test that reaches the handler requires a real SQL Server: the account-level lock
/// (SqlServerAccountCredentialLock) issues sp_getapplock, which the EF InMemory provider cannot
/// execute. Only the pre-handler 401 check needs no database at all and runs unconditionally.
/// </summary>
public sealed class ChangePasswordEndpointsTests
{
    private const string MobileRoute = "/api/v1/auth/change-password";
    private const string WebRoute = "/api/v1/auth/web/change-password";

    [Fact]
    public async Task Mobile_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Rejected by authentication middleware before the handler/database is ever reached —
        // the only ChangePassword endpoint assertion that does not need a real SQL Server.
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Mobile_WithMissingFields_ReturnsBadRequestMsg01()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "", newPassword = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG01");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Mobile_WithWrongCurrentPassword_ReturnsBadRequestMsg17()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "WrongPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG17");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Mobile_WithWeakNewPassword_ReturnsBadRequestMsg05()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "weak" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG05");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Mobile_WithNewPasswordEqualToCurrent_ReturnsBadRequestMsg05()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "CorrectPass1!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("MSG05");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Mobile_WithNoLocalPassword_ReturnsForbidden()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, password: null);
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "Anything1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Mobile_WithValidRequest_ReplacesSessionAndRevokesPriorTokens()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        await SeedRefreshTokenAsync(database, userId);
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            MobileRoute, new { currentPassword = "CorrectPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();

        await using var verification = database.CreateDbContext();
        var tokens = await verification.RefreshTokens.Where(t => t.UserId == userId).ToListAsync();
        tokens.Should().HaveCount(2);
        tokens.Count(t => t.RevokedAtUtc == null).Should().Be(1);

        var hasher = new PasswordHasherService();
        var updated = await verification.Users.SingleAsync(u => u.Id == userId);
        hasher.Verify("NewPass2@", updated.PasswordHash!).Should().BeTrue();
        hasher.Verify("CorrectPass1!", updated.PasswordHash!).Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Web_WithValidRequest_SetsCookieAndOmitsRefreshTokenFromJson()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            WebRoute, new { currentPassword = "CorrectPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(cookie => cookie.StartsWith("tripmate_refresh=", StringComparison.Ordinal));

        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.TryGetProperty("refreshToken", out _).Should().BeFalse();
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Web_WithWrongCurrentPassword_PreservesExistingSession()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var userId = await SeedUserAsync(database, "CorrectPass1!");
        await SeedRefreshTokenAsync(database, userId);
        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            WebRoute, new { currentPassword = "WrongPass1!", newPassword = "NewPass2@" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();

        await using var verification = database.CreateDbContext();
        (await verification.RefreshTokens.Where(t => t.UserId == userId).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
    }

    private static async Task<long> SeedUserAsync(SqlServerTestDatabase database, string? password)
    {
        await using var context = database.CreateDbContext();
        var hasher = new PasswordHasherService();
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
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private static async Task SeedRefreshTokenAsync(SqlServerTestDatabase database, long userId)
    {
        await using var context = database.CreateDbContext();
        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = $"hashed:seed-{Guid.NewGuid()}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        });
        await context.SaveChangesAsync();
    }
}
