using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class ServiceTripReviewEndpointSqlServerTests
{
    [SqlServerFact]
    public async Task CompletedOwnedService_CreateGetAndEdit_UsesPoiPolicyAndOriginalDeadline()
    {
        await using var database = await SeedAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        const string route = "/api/v1/service-bookings/1/review";

        using var before = await client.GetAsync(route);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await before.Content.ReadAsStringAsync()))
        {
            var reviewableRecord = body.RootElement.GetProperty("reviewableRecord");
            reviewableRecord.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo(["kind", "id"]);
            reviewableRecord.GetProperty("kind").GetString()
                .Should().Be(ReviewableRecordRef.ServiceBookingKind);
            body.RootElement.TryGetProperty("bookingId", out _).Should().BeFalse();
            body.RootElement.GetProperty("serviceBookingId").GetInt64().Should().Be(1);
            var subject = body.RootElement.GetProperty("subject");
            subject.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo(["kind", "id"]);
            subject.GetProperty("kind").GetString().Should().Be("poi");
            subject.GetProperty("id").GetInt64().Should().Be(1);
            body.RootElement.GetProperty("summary").GetProperty("name").GetString()
                .Should().Be("Service One");
            body.RootElement.GetProperty("summary").GetProperty("bookingReference").GetString()
                .Should().Be("SB-ONE");
        }

        using var created = await PostAsync(client, route, "Great stay", "Helpful staff and a clean room.");
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var deadline = createdBody.RootElement.GetProperty("editDeadlineUtc").GetDateTimeOffset();
        var version = createdBody.RootElement.GetProperty("version").GetString();
        var createdRecord = createdBody.RootElement.GetProperty("reviewableRecord");
        createdRecord.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["kind", "id"]);
        createdRecord.GetProperty("kind").GetString()
            .Should().Be(ReviewableRecordRef.ServiceBookingKind);
        createdBody.RootElement.TryGetProperty("bookingId", out _).Should().BeFalse();
        createdBody.RootElement.GetProperty("serviceBookingId").GetInt64().Should().Be(1);
        createdBody.RootElement.GetProperty("subject").EnumerateObject()
            .Select(property => property.Name).Should().BeEquivalentTo(["kind", "id"]);

        using var duplicate = await PostAsync(client, route, "Again", "This must not create another review.");
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, TripReviewErrorCodes.Duplicate);

        using var edit = await client.PutAsJsonAsync(route, new
        {
            overallRating = 4,
            title = "Updated stay",
            content = "The room was clean and the service remained helpful.",
            publishDisplayName = false,
            version,
        });
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        using var editBody = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        editBody.RootElement.GetProperty("editDeadlineUtc").GetDateTimeOffset().Should().Be(deadline);
        editBody.RootElement.GetProperty("version").GetString().Should().NotBe(version);

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews WHERE service_booking_id=1 AND booking_id IS NULL")).Should().Be(1);
        (await database.ExecuteScalarAsync<string>("SELECT policy_version FROM social.TripReviews WHERE service_booking_id=1"))
            .Should().Be(ReviewContentPolicy.ActiveVersion);
        (await database.ExecuteScalarAsync<long>("SELECT poi_id FROM social.TripReviews WHERE service_booking_id=1"))
            .Should().Be(1);
    }

    [SqlServerFact]
    public async Task ServiceOwnershipCompletionAndNullPoi_AreEnforcedWithoutDisclosure()
    {
        await using var database = await SeedAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var foreign = await client.GetAsync("/api/v1/service-bookings/3/review");
        await AssertProblemAsync(foreign, HttpStatusCode.NotFound, TripReviewErrorCodes.BookingNotFound);

        using var incomplete = await PostAsync(client, "/api/v1/service-bookings/2/review",
            "Not complete", "This booking is not completed yet.");
        await AssertProblemAsync(incomplete, HttpStatusCode.Conflict, TripReviewErrorCodes.BookingNotCompleted);

        using var unsupported = await client.GetAsync("/api/v1/service-bookings/6/review");
        await AssertProblemAsync(unsupported, HttpStatusCode.Conflict, TripReviewErrorCodes.UnsupportedSubject);
    }

    [SqlServerFact]
    public async Task RejectedAndUnavailableServiceText_ReturnSanitizedProblemsAndPublishNothing()
    {
        await using var database = await SeedAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var rejected = await PostAsync(client, "/api/v1/service-bookings/4/review",
            "Threat", "I will find the guide and hurt him.");
        await AssertProblemAsync(rejected, HttpStatusCode.BadRequest, TripReviewErrorCodes.PolicyRejected);
        (await rejected.Content.ReadAsStringAsync()).Should().NotContain("hurt him");

        using var unavailable = await PostAsync(client, "/api/v1/service-bookings/5/review",
            "旅行", "この旅行についての感想です。");
        await AssertProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable,
            TripReviewErrorCodes.PolicyUnavailable);

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviews WHERE service_booking_id IN (4,5)"))
            .Should().Be(0);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviewMediaOperations WHERE service_booking_id IN (4,5)"))
            .Should().Be(0);
    }

    private static async Task<SqlServerTestDatabase> SeedAsync()
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteNonQueryAsync("""
                INSERT dbo.Users(role,status,email,full_name)
                VALUES('Traveler','Active',N'owner@test.invalid',N'Owner'),
                      ('Traveler','Active',N'foreign@test.invalid',N'Foreign');
                INSERT catalog.POICategories(name) VALUES(N'Test category');
                INSERT catalog.POIs(category_id,name,latitude,longitude)
                VALUES(1,N'Canonical POI',16,108);
                INSERT commercial.ServiceProviders(name,service_category)
                VALUES(N'Provider','Hotel');
                INSERT commercial.Services(provider_id,service_category,name,poi_id,price_amount,price_unit)
                VALUES(1,'Hotel',N'Service One',1,0,'PerNight'),
                      (1,'Hotel',N'No POI Service',NULL,0,'PerNight');
                INSERT commercial.ServiceBookings(traveler_user_id,service_id,start_datetime,total_price,status,provider_reference)
                VALUES(1,1,'2026-10-01T02:00:00',0,'Completed',N'SB-ONE'),
                      (1,1,'2026-10-02T02:00:00',0,'Confirmed',N'SB-TWO'),
                      (2,1,'2026-10-03T02:00:00',0,'Completed',N'SB-THREE'),
                      (1,1,'2026-10-04T02:00:00',0,'Completed',N'SB-FOUR'),
                      (1,1,'2026-10-05T02:00:00',0,'Completed',N'SB-FIVE'),
                      (1,2,'2026-10-06T02:00:00',0,'Completed',N'SB-SIX');
                """);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string route, string title, string content)
    {
        using var multipart = new MultipartFormDataContent();
        var metadata = JsonSerializer.Serialize(new
        {
            overallRating = 5,
            title,
            content,
            poiRatings = Array.Empty<object>(),
            publishDisplayName = false,
        });
        multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata");
        return await client.PostAsync(route, multipart);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }
}