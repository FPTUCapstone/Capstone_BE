using System.Net;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

[Collection(nameof(TripMateApiFactory))]
public class ListTourOperatorApplicationsEndpointTests
{
    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Get_NonStaff_IsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, role);
        var response = await client.GetAsync("/api/v1/admin/tour-operator-applications");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Staff_CanReadQueue()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);
        var response = await client.GetAsync("/api/v1/admin/tour-operator-applications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_Anonymous_IsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/api/v1/admin/tour-operator-applications");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}