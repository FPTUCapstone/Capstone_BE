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

using Xunit;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// POST /api/v1/auth/refresh: the Mobile counterpart of the Web cookie refresh. The refresh
/// token travels in the body; the session row is redeemed without rotation.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public class MobileRefreshIntegrationTests
{
    [Fact]
    public async Task Post_Refresh_WithLoginRefreshToken_IssuesANewAccessToken()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory);
        var login = await LoginAsync(client);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("refreshToken").GetString().Should().Be(login.RefreshToken);
        data.GetProperty("role").GetString().Should().Be("Traveler");
        data.GetProperty("accessTokenExpiresAtUtc").GetDateTimeOffset()
            .Should().BeAfter(DateTimeOffset.UtcNow);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().BeNull();
            return true;
        });
    }

    [Fact]
    public async Task Post_Refresh_NewAccessTokenAuthorizesTravelerRequests()
    {
        // Real JWT validation, so the refreshed token itself is what authorizes the call.
        using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();
        await Seed(factory);
        var login = await LoginAsync(client);
        using var refresh = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });
        var accessToken = (await refresh.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("accessToken").GetString();

        // Any Traveler-only endpoint proves the refreshed token carries the Traveler role.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/points-of-interest/search?latitude=16.043&longitude=108.222&radiusKm=5&page=1&pageSize=20");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-known-refresh-token")]
    public async Task Post_Refresh_WithMissingOrUnknownToken_IsUnauthorized(string? refreshToken)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task Post_Refresh_AfterMobileSignOut_IsUnauthorized()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory);
        var login = await LoginAsync(client);
        using var logout = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = login.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_Refresh_ForALockedAccount_IsRejected()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory);
        var login = await LoginAsync(client);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync()).Status = AccountStatus.Locked;
            await db.SaveChangesAsync();
            return true;
        });

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<(string AccessToken, string RefreshToken)> LoginAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "mobile-refresh@example.com", password = "CorrectPass1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return (data.GetProperty("accessToken").GetString()!, data.GetProperty("refreshToken").GetString()!);
    }

    private static Task<bool> Seed(TripMateApiFactory factory) => factory.WithDbContextAsync(async db =>
    {
        using var scope = factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        db.Users.Add(new User
        {
            Email = "mobile-refresh@example.com",
            FullName = "Mobile Refresh Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash("CorrectPass1"),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
        return true;
    });
}