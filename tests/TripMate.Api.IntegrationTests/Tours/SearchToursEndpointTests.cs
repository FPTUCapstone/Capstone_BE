using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class SearchToursEndpointTests
{
    [Fact]
    public async Task Search_AsGuest_ReturnsDirectContractAndNoStore()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/tours");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.TryGetProperty("data", out _).Should().BeFalse();
        root.TryGetProperty("success", out _).Should().BeFalse();
        root.GetProperty("page").GetInt32().Should().Be(1);
        root.GetProperty("pageSize").GetInt32().Should().Be(20);
        root.GetProperty("totalCount").GetInt64().Should().Be(0);
        root.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("page=", "page")]
    [InlineData("page=abc", "page")]
    [InlineData("departureDate=2026-09-20T00%3A00%3A00Z", "departureDate")]
    [InlineData("minPrice=1.5", "minPrice")]
    [InlineData("page=1&page=2", "page")]
    [InlineData("pageSize=%20", "pageSize")]
    [InlineData("sort=price", "query")]
    public async Task Search_InvalidRawQuery_ReturnsFieldLevelProblemDetails(
        string rawQuery,
        string expectedField)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours?{rawQuery}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty(expectedField, out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Search_SemanticallyInvalidQuery_UsesCamelCaseValidationKey()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/tours?pageSize=101");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty("pageSize", out _)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("departureDate=&minPrice=&maxPrice=")]
    [InlineData("destination=%20%20%20")]
    public async Task Search_EmptyOptionalCriteria_AreTreatedAsAbsent(string rawQuery)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours?{rawQuery}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("minPrice=%2B1", "minPrice")]
    [InlineData("maxPrice=-1", "maxPrice")]
    [InlineData("page=%2B1", "page")]
    [InlineData("Page=1", "page")]
    [InlineData("page=1&Page=2", "page")]
    public async Task Search_NonCanonicalNumericQuery_IsRejected(
        string rawQuery,
        string expectedField)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/tours?{rawQuery}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty(expectedField, out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Search_WithInvalidBearerToken_RemainsPublic()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "not-a-jwt");

        using var response = await client.GetAsync("/api/v1/tours");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_EndToEnd_UsesRealSqlDataAndPreservesBigIntIdsAsStrings()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT INTO dbo.Users (user_id, role, email, full_name, status)
            VALUES (301, 'TourOperator', N'endpoint@example.test', N'Endpoint operator', 'Active');
            SET IDENTITY_INSERT dbo.Users OFF;

            INSERT INTO dbo.OperatorProfiles
                (user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (301, N'Endpoint Travel', N'TAX-301', N'LIC-301', 'Approved');

            SET IDENTITY_INSERT commerce.Tours ON;
            INSERT INTO commerce.Tours
                (tour_id, operator_user_id, title, destination, base_price,
                 duration_days, status, published_at)
            VALUES
                (9007199254740995, 301, N'Hoi An public tour', N'Hội An', 800000, 2,
                 'Approved', '2020-01-01T00:00:00');
            SET IDENTITY_INSERT commerce.Tours OFF;

            INSERT INTO catalog.Destinations (name) VALUES (N'Hội An'), (N'Đà Nẵng');
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 9007199254740995, destination_id,
                CASE WHEN name = N'Đà Nẵng' THEN 1 ELSE 2 END
            FROM catalog.Destinations;

            SET IDENTITY_INSERT commerce.TourSchedules ON;
            INSERT INTO commerce.TourSchedules
                (schedule_id, tour_id, start_datetime, end_datetime,
                 total_capacity, reserved_capacity, status)
            VALUES
                (9007199254740997, 9007199254740995,
                 '2090-01-01T17:00:00', '2090-01-03T17:00:00', 15, 4, 'Scheduled');
            SET IDENTITY_INSERT commerce.TourSchedules OFF;
            """);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/tours?destination=H%E1%BB%99i%20An&departureDate=2090-01-02"
            + "&minPrice=800000&maxPrice=800000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("tourId").GetString().Should().Be("9007199254740995");
        item.GetProperty("destinations").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("Đà Nẵng", "Hội An");
        item.GetProperty("representativeScheduleId").GetString()
            .Should().Be("9007199254740997");
        item.GetProperty("departureAtUtc").GetString().Should().EndWith("Z");
        item.GetProperty("remainingSlots").GetInt32().Should().Be(11);
        body.RootElement.GetProperty("asOfUtc").GetString().Should().EndWith("Z");
    }
}