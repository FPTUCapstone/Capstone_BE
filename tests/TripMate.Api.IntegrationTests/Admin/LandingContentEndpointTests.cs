using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

[Collection(nameof(TripMateApiFactory))]
public sealed class LandingContentEndpointTests
{
    [Fact]
    public async Task Get_WithTravelerRole_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/admin/landing-content");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PublicGet_WithoutAuthentication_ReturnsOnlyTheLatestPublishedSnapshot()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();
        await SeedPublishedVersionAsync(factory, "Published");
        await factory.WithDbContextAsync(async db =>
        {
            db.LandingContentVersions.Add(LandingContentVersion.CreateDraft(
                JsonSerializer.Serialize(Content("Private draft")), 1, 100,
                new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero)));
            return await db.SaveChangesAsync();
        });

        var response = await client.GetAsync("/api/v1/landing-content");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("versionNumber").GetInt32().Should().Be(1);
        root.GetProperty("content").GetProperty("hero").GetProperty("headline").GetString().Should().Be("Published");
        root.TryGetProperty("draft", out _).Should().BeFalse();
        root.TryGetProperty("history", out _).Should().BeFalse();
        root.TryGetProperty("publishedByName", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DraftThenPublish_WithAdministrator_ReturnsTheNextImmutableVersion()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Administrator);
        await SeedPublishedVersionAsync(factory, "Before");

        var save = await client.PutAsync("/api/v1/admin/landing-content/draft", JsonBody(new
        {
            baseVersion = 1,
            content = Content("After"),
        }));
        var publish = await client.PostAsync("/api/v1/admin/landing-content/publish", JsonBody(new { baseVersion = 1 }));

        save.StatusCode.Should().Be(HttpStatusCode.OK);
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await publish.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("versionNumber").GetInt32().Should().Be(2);
        body.RootElement.GetProperty("content").GetProperty("hero").GetProperty("headline").GetString().Should().Be("After");
        (await factory.WithDbContextAsync(db => db.LandingContentVersions.CountAsync(version => !version.IsPublished))).Should().Be(0);
    }

    [Fact]
    public async Task Upload_WithoutFile_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = CreateClient(factory, UserRole.Administrator);
        using var form = new MultipartFormDataContent();

        var response = await client.PostAsync("/api/v1/admin/landing-content/media", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task SeedPublishedVersionAsync(TripMateApiFactory factory, string headline)
    {
        await factory.WithDbContextAsync(async db =>
        {
            db.LandingContentVersions.Add(LandingContentVersion.CreateDraft(
                JsonSerializer.Serialize(Content(headline)), 1, 100,
                new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)).PublishAsNextVersion(
                100, 1, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)));
            return await db.SaveChangesAsync();
        });
    }

    private static object Content(string headline) => new
    {
        hero = new { headline, subHeadline = string.Empty, backgroundImage = (object?)null, ctaLabel = "Explore", ctaTarget = "/tours" },
        destinations = Array.Empty<object>(),
        featuredTourIds = Array.Empty<string>(),
        banner = new { image = (object?)null, link = string.Empty, displayStart = string.Empty, displayEnd = string.Empty },
    };

    private static HttpClient CreateClient(TripMateApiFactory factory, UserRole role)
    {
        var service = factory.Services.GetRequiredService<IJwtTokenService>();
        var token = service.GenerateAccessToken(new User
        {
            Id = 100,
            Email = "landing-admin@example.com",
            FullName = "Landing Administrator",
            Role = role,
            Status = AccountStatus.Active,
        }).Token;
        return factory.CreateJwtClient(token);
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}