using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.StatisticalReports;

[Collection(nameof(TripMateApiFactory))]
public sealed class StatisticalReportOperatorsEndpointTests
{
    [Fact]
    public async Task GetOperators_Traveler_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/admin/statistical-reports/operators");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOperators_Administrator_ReturnsOnlyApprovedOperatorsInCamelCase()
    {
        await using var factory = new TripMateApiFactory();
        await SeedOperatorAsync(factory, 60, "Oceanic Travel Co.", OperatorApprovalStatus.Approved);
        await SeedOperatorAsync(factory, 61, "Pending Co", OperatorApprovalStatus.PendingApproval);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/statistical-reports/operators");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetArrayLength().Should().Be(1);
        body[0].GetProperty("id").GetInt64().Should().Be(60);
        body[0].GetProperty("name").GetString().Should().Be("Oceanic Travel Co.");
    }

    private static async Task SeedOperatorAsync(
        TripMateApiFactory factory,
        long userId,
        string companyName,
        OperatorApprovalStatus status)
    {
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Id = userId,
                FullName = companyName,
                Email = $"operator-{userId}@example.com",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(CancellationToken.None);

            db.OperatorProfiles.Add(new OperatorProfile
            {
                User = user,
                CompanyName = companyName,
                TaxCode = $"TAX-{userId}",
                BusinessLicenseNo = $"LIC-{userId}",
                ApprovalStatus = status,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            return 0;
        });
    }
}