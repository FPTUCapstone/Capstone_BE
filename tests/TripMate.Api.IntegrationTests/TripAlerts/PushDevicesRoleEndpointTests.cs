using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TripAlerts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PushDevicesRoleEndpointTests
{
    private static async Task SeedAsync(TripMateApiFactory factory, long id, UserRole role, bool approved)
    {
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Id = id,
                Email = $"push-endpoint-{id}@test.local",
                FullName = $"Push {id}",
                Role = role,
                Status = AccountStatus.Active,
            };
            db.Users.Add(user);
            if (role == UserRole.TourOperator)
            {
                db.OperatorProfiles.Add(new OperatorProfile
                {
                    UserId = id,
                    User = user,
                    CompanyName = $"Operator {id}",
                    TaxCode = $"TAX-{id}",
                    BusinessLicenseNo = $"LICENSE-{id}",
                    ApprovalStatus = approved
                        ? OperatorApprovalStatus.Approved
                        : OperatorApprovalStatus.PendingApproval,
                });
            }

            await db.SaveChangesAsync();
            return true;
        });
    }

    [Fact]
    public async Task ApprovedOperator_RegistersRotatesAndRemovesADevice()
    {
        using var factory = new TripMateApiFactory();
        await SeedAsync(factory, 901, UserRole.TourOperator, approved: true);
        using var client = factory.CreateAuthenticatedClient(901, UserRole.TourOperator);
        var path = $"/api/v1/me/push-devices/{Guid.NewGuid()}";

        (await client.PutAsJsonAsync(path, new { token = "operator-token" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync(path, new { token = "rotated-token" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stored = await factory.WithDbContextAsync(db => db.Set<PushDeviceToken>().SingleAsync());
        stored.UserId.Should().Be(901);
        stored.DeactivatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task UnapprovedOperator_IsForbidden()
    {
        using var factory = new TripMateApiFactory();
        await SeedAsync(factory, 902, UserRole.TourOperator, approved: false);
        using var client = factory.CreateAuthenticatedClient(902, UserRole.TourOperator);

        (await client.PutAsJsonAsync(
                $"/api/v1/me/push-devices/{Guid.NewGuid()}", new { token = "operator-token" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Administrator_IsForbidden()
    {
        using var factory = new TripMateApiFactory();
        await SeedAsync(factory, 903, UserRole.Administrator, approved: false);
        using var client = factory.CreateAuthenticatedClient(903, UserRole.Administrator);

        (await client.PutAsJsonAsync(
                $"/api/v1/me/push-devices/{Guid.NewGuid()}", new { token = "admin-token" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
