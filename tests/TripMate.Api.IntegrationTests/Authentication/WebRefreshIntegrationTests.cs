using System.Net;
using System.Net.Http.Headers;
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
/// S01: POST /api/v1/auth/web/refresh redeems the tripmate_refresh cookie for the
/// authoritative current Web auth context. Cookie is the only credential; no body.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public class WebRefreshIntegrationTests
{
    private static async Task<string> LoginAndGetCookie(HttpClient client, IJwtTokenService jwt, string email = "login@example.com")
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/web/login", new { email, password = "CorrectPass1", keepMeSignedIn = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await ReadCookie(response);
        return raw;
    }

    private static async Task<string> ReadCookie(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        return Uri.UnescapeDataString(cookie.Split(';')[0].Split('=', 2)[1]);
    }

    [Fact]
    public async Task Post_Refresh_WithLoginCookie_ReturnsContextWithoutNewCookie()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Traveler);
        using var scope = factory.Services.CreateScope();
        var cookie = await LoginAndGetCookie(client, scope.ServiceProvider.GetRequiredService<IJwtTokenService>());

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        request.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(cookie)}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("Active");
        data.GetProperty("role").GetString().Should().Be("Traveler");
        data.GetProperty("applicationStatus").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        data.TryGetProperty("refreshToken", out _).Should().BeFalse();
        // Non-rotating: the original session row is untouched.
        await factory.WithDbContextAsync(async db =>
        {
            var token = await db.RefreshTokens.SingleAsync();
            token.RevokedAtUtc.Should().BeNull();
            return true;
        });
    }

    [Theory]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Approved, "Approved")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.PendingApproval, "PendingApproval")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Rejected, "Rejected")]
    public async Task Post_Refresh_ReturnsCurrentOperatorApplicationStatus(UserRole role, OperatorApprovalStatus approval, string expected)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, role, approval);
        using var scope = factory.Services.CreateScope();
        var cookie = await LoginAndGetCookie(client, scope.ServiceProvider.GetRequiredService<IJwtTokenService>());

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        request.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(cookie)}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("applicationStatus").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Post_Refresh_ReflectsApprovalChangeMadeAfterLogin()
    {
        // The refresh context is current DB state, not login-time claims.
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.TourOperator, OperatorApprovalStatus.PendingApproval);
        using var scope = factory.Services.CreateScope();
        var cookie = await LoginAndGetCookie(client, scope.ServiceProvider.GetRequiredService<IJwtTokenService>());

        await factory.WithDbContextAsync(async db =>
        {
            db.OperatorProfiles.Single().ApprovalStatus = OperatorApprovalStatus.Approved;
            await db.SaveChangesAsync();
            return true;
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        request.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(cookie)}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("applicationStatus").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task Post_Refresh_WithoutCookie_IsUnauthorized()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/auth/web/refresh", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task Post_Refresh_WithUnknownCookie_IsUnauthorized()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        request.Headers.Add("Cookie", "tripmate_refresh=forged-value");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task Post_Refresh_WithRevokedCookie_IsUnauthorized()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        await Seed(factory, UserRole.Traveler);
        using var scope = factory.Services.CreateScope();
        var cookie = await LoginAndGetCookie(client, scope.ServiceProvider.GetRequiredService<IJwtTokenService>());
        await factory.WithDbContextAsync(async db =>
        {
            db.RefreshTokens.Single().RevokedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return true;
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/refresh");
        request.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(cookie)}");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static Task<bool> Seed(TripMateApiFactory factory, UserRole role, OperatorApprovalStatus? approval = null) => factory.WithDbContextAsync(async db =>
    {
        using var scope = factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var user = new User
        {
            Email = "login@example.com",
            FullName = "Seed User",
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