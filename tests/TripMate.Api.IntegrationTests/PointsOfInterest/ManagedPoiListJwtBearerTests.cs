using System.Net;
using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Management;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class ManagedPoiListJwtBearerTests
{
    private const string Route = "/api/v1/admin/pois";

    [Theory]
    [InlineData(TestJwtKind.Malformed)]
    [InlineData(TestJwtKind.Expired)]
    [InlineData(TestJwtKind.InvalidSignature)]
    [InlineData(TestJwtKind.InvalidIssuer)]
    public async Task Get_InvalidJwt_Returns401WithBearerChallengeAndNoStore(TestJwtKind kind)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateJwtClient(TestJwtTokenFactory.Create(kind));
        var response = await client.GetAsync(Route);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Select(v => v.Scheme).Should().Contain("Bearer");
        ManagedPoiListEndpointTests.AssertNoStore(response);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Administrator)]
    public async Task Get_ProductionJwtForCurrentActiveManager_Returns200(UserRole role)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var actor = await SeedAsync(factory, role, AccountStatus.Active);
        using var client = factory.CreateJwtClient(factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(actor).Token);
        var response = await client.GetAsync(Route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ManagedPoiListEndpointTests.AssertNoStore(response);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Get_ProductionJwtWithWrongRole_Returns403(UserRole role)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var actor = await SeedAsync(factory, role, AccountStatus.Active);
        using var client = factory.CreateJwtClient(factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(actor).Token);
        var response = await client.GetAsync(Route);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ManagedPoiListEndpointTests.AssertNoStore(response);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Get_StaleManagerJwt_DeniesInactiveChangedRoleOrMissingCurrentActor(bool roleChanged, bool missingActor)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var actor = await SeedAsync(factory, UserRole.Staff, AccountStatus.Active);
        var token = factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(actor).Token;
        await factory.WithDbContextAsync(async db =>
        {
            var stored = await db.Users.FindAsync(actor.Id);
            if (missingActor) db.Users.Remove(stored!);
            else if (roleChanged) stored!.Role = UserRole.Traveler;
            else stored!.Status = AccountStatus.Inactive;
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateJwtClient(token);
        var response = await client.GetAsync(Route);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ManagedPoiListEndpointTests.AssertNoStore(response);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ManagedPoiErrors.AccessRequired);
    }

    private static Task<User> SeedAsync(TripMateApiFactory factory, UserRole role, AccountStatus status) => factory.WithDbContextAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var actor = new User { Email = $"{Guid.NewGuid():N}@example.com", FullName = "JWT management actor", Role = role, Status = status, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        return actor;
    });
}