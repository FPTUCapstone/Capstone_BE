using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreatePoiJwtBearerTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TestJwtKind.Malformed)]
    [InlineData(TestJwtKind.Expired)]
    [InlineData(TestJwtKind.InvalidSignature)]
    [InlineData(TestJwtKind.InvalidIssuer)]
    public async Task Post_WithInvalidJwt_ReturnsEmptyUnauthorized(TestJwtKind tokenKind)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateJwtClient(TestJwtTokenFactory.Create(tokenKind));

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/pois",
            ValidBody(categoryId: 1));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Select(value => value.Scheme)
            .Should().Contain("Bearer");
        response.Content.Headers.ContentType.Should().BeNull();
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Post_WithProductionJwtForActiveAdministrator_ReturnsCreated()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var seed = await SeedAsync(factory);
        var tokenService = factory.Services.GetRequiredService<IJwtTokenService>();
        var token = tokenService.GenerateAccessToken(seed.Administrator).Token;
        using var client = factory.CreateJwtClient(token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/pois",
            ValidBody(seed.CategoryId));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static object ValidBody(int categoryId) => new
    {
        name = "JWT Pipeline POI",
        categoryId,
        latitude = 11.941755m,
        longitude = 108.438278m,
    };

    private static Task<SeedResult> SeedAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async context =>
        {
            var administrator = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "JWT Pipeline Administrator",
                Role = UserRole.Administrator,
                Status = AccountStatus.Active,
                CreatedAtUtc = SeedTime,
                UpdatedAtUtc = SeedTime,
            };
            var category = PoiCategory.Create("JWT Test Category", null);

            context.Users.Add(administrator);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();

            return new SeedResult(administrator, category.Id);
        });

    private sealed record SeedResult(User Administrator, int CategoryId);
}