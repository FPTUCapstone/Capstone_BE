using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutSettlementsOpenApiTests
{
    [Fact]
    public async Task Queue_DocumentsQueryParametersAndStatusCodes()
    {
        using var document = await LoadDocumentAsync();
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admin/payout-settlements").GetProperty("get");

        var names = operation.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
        names.Should().Contain(["keyword", "status", "periodFrom", "periodTo", "pageNumber", "pageSize"]);
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "401", "403", "500" })
        {
            responses.TryGetProperty(status, out _).Should().BeTrue($"HTTP {status} is part of the contract");
        }
    }

    [Fact]
    public async Task Detail_DocumentsStatusCodesAndTheOutcomeSchema()
    {
        using var document = await LoadDocumentAsync();
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admin/payout-settlements/{payoutId}").GetProperty("get");

        foreach (var status in new[] { "200", "401", "403", "404", "500" })
        {
            operation.GetProperty("responses").TryGetProperty(status, out _).Should().BeTrue($"HTTP {status} is part of the contract");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var detail = schemas.GetProperty("PayoutSettlementDetailDto").GetProperty("properties");
        detail.TryGetProperty("bankBinResolved", out _).Should().BeTrue();
        detail.TryGetProperty("outcome", out _).Should().BeTrue();
        var outcome = schemas.GetProperty("PayoutSettlementOutcomeDto").GetProperty("properties");
        foreach (var name in new[] { "gatewayState", "failureCode", "submittedAtUtc", "reconciliationRequired" })
        {
            outcome.TryGetProperty(name, out _).Should().BeTrue($"{name} is part of the outcome contract");
        }

        var json = detail.GetRawText().ToLowerInvariant();
        json.Should().NotContain("accountnumber").And.NotContain("gatewayreference").And.NotContain("bankbin\"");
    }

    [Fact]
    public async Task Authorize_IsAPostReturning202WithTheDocumentedFailureCodes()
    {
        using var document = await LoadDocumentAsync();
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admin/payout-settlements/{payoutId}/authorize").GetProperty("post");

        foreach (var status in new[] { "202", "401", "403", "404", "409", "422", "500" })
        {
            operation.GetProperty("responses").TryGetProperty(status, out _).Should().BeTrue($"HTTP {status} is part of the contract");
        }

        operation.TryGetProperty("requestBody", out _).Should().BeFalse("authorize takes no body");
        var response = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("AuthorizePayoutSettlementResponseDto").GetProperty("properties");
        foreach (var name in new[] { "payoutId", "payoutCode", "status", "authorizedAtUtc" })
        {
            response.TryGetProperty(name, out _).Should().BeTrue();
        }
    }

    private async Task<JsonDocument> LoadDocumentAsync()
    {
        await using var factory = new TripMateApiFactory();
        var provider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await provider.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        return JsonDocument.Parse(json);
    }
}