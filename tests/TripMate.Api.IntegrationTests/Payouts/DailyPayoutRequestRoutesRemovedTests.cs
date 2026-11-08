using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

// Task 1 (DAILY-PAYOUT-TRANSFER): the operator payout request flow and the staff/admin
// approve/reject flow are removed from SRS v11. The OpenAPI contract must not expose them.
// Checking the contract avoids false passes from 404s on non-existent payout identifiers.
//
// The old request-model read side (Admin list/detail, Operator history) was left in place when
// Task 1 ran and only removed afterwards, once the FE's UC-46 screens calling them were deleted
// (see the FE branch's Task 0) and nothing else in the new daily-settlement code read them —
// replaced by AdminPayoutSettlementsController/OperatorPayoutSettlementsController.
[Collection(nameof(TripMateApiFactory))]
public sealed class DailyPayoutRequestRoutesRemovedTests
{
    private static readonly string[] RemovedOperations =
    {
        "/api/v1/operator/payouts:post",
        "/api/v1/operator/payouts/{payoutCode}/cancel:post",
        "/api/v1/operator/payouts/available-balance:get",
        "/api/v1/operator/payouts/settlement-months:get",
        "/api/v1/admin/payouts/{payoutId}/approve:post",
        "/api/v1/admin/payouts/{payoutId}/reject:post",
        "/api/v1/admin/payouts:get",
        "/api/v1/admin/payouts/{payoutId}:get",
        "/api/v1/operator/payouts:get",
    };

    [Fact]
    public async Task OpenApi_DoesNotExposeRemovedPayoutRequestOperations()
    {
        await using var factory = new TripMateApiFactory();
        var provider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await provider.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");

        var exposed = new List<string>();
        foreach (var entry in RemovedOperations)
        {
            var parts = entry.Split(':');
            if (paths.TryGetProperty(parts[0], out var path) && path.TryGetProperty(parts[1], out _))
            {
                exposed.Add(entry);
            }
        }

        exposed.Should().BeEmpty("these operations belong to the removed request/approval flow");
    }
}
