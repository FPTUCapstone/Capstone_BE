using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.Authorization;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Reviews;

[Collection(nameof(TripMateApiFactory))]
public sealed class TripReviewEndpointTests
{
    private const string Route = "/api/v1/bookings/1/review";
    private static readonly DateTimeOffset Created = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task ReviewRoutes_AuthenticateBeforeBindingOrBusinessDisclosure(string method)
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateClient();
        using var request = Request(method);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(401);
        body.RootElement.GetProperty("errorCode").GetString()
            .Should().Be("trip_review.unauthorized");
    }

    [Theory]
    [InlineData(TestJwtKind.Malformed)]
    [InlineData(TestJwtKind.Expired)]
    [InlineData(TestJwtKind.InvalidSignature)]
    [InlineData(TestJwtKind.InvalidIssuer)]
    public async Task Get_InvalidJwt_ReturnsFeatureUnauthorizedProblem(TestJwtKind tokenKind)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateJwtClient(TestJwtTokenFactory.Create(tokenKind));

        using var response = await client.GetAsync(Route);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized,
            "trip_review.unauthorized");
    }

    [Fact]
    public async Task WrongRole_IsForbiddenBeforeRequestBinding()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.TourOperator);
        using var response = await client.PostAsync(Route, new StringContent("not-multipart"));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "trip_review.forbidden");
    }

    [SqlServerFact]
    public async Task InactiveTraveler_IsForbiddenBeforePostAndPutBodyParsing()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        using var post = await client.PostAsync(Route,
            new StringContent("not-multipart", null, "text/plain"));
        using var put = await client.PutAsync(Route,
            new StringContent("not-json", null, "multipart/form-data"));
        using var oversizedContent = new ByteArrayContent(new byte[26_000_001]);
        oversizedContent.Headers.ContentType = new("multipart/form-data")
        {
            Parameters = { new("boundary", "inactive-test") },
        };
        using var oversized = await client.PostAsync(Route, oversizedContent);

        await AssertProblemAsync(post, HttpStatusCode.Forbidden, "trip_review.forbidden");
        await AssertProblemAsync(put, HttpStatusCode.Forbidden, "trip_review.forbidden");
        await AssertProblemAsync(oversized, HttpStatusCode.Forbidden, "trip_review.forbidden");
    }

    [SqlServerFact]
    public async Task ValidSignedTravelerJwt_UsesTheRealAuthorizationPath()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(
            database,
            Created,
            authenticationMode: ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateJwtClient(
            TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Post_RejectsUnsupportedContentType(string contentType)
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new StringContent("{}", null, contentType);
        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType,
            "trip_review.unsupported_media_type");
    }

    [Fact]
    public async Task Post_RejectsMalformedMetadataBeforeApplicationWork()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart("not-json");
        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_review.invalid_input");
    }

    [Theory]
    [InlineData("\"overallRating\": 0,", "\"overallRating\": 5,")]
    [InlineData("\"title\": \"   \",", "\"title\": \"Title\",")]
    [InlineData("\"poiRatings\": [{\"poiId\": 0, \"rating\": 6}]", "\"poiRatings\": []")]
    public async Task Post_ValidationFailure_ReturnsFeatureProblemDetails(
        string replacement,
        string original)
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata().Replace(original, replacement,
            StringComparison.Ordinal));
        using var response = await client.PostAsync(Route, content);

        await AssertValidationProblemAsync(response, "trip_review.invalid_input");
    }

    [Fact]
    public async Task Post_EmptyFileValidation_ReturnsFeatureProblemDetails()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata());
        var file = new ByteArrayContent([]);
        file.Headers.ContentType = new("image/jpeg");
        content.Add(file, "files", "empty.jpg");

        using var response = await client.PostAsync(Route, content);

        await AssertValidationProblemAsync(response, "trip_review.invalid_input");
    }

    [SqlServerFact]
    public async Task Post_RejectsNonJsonOrNonUtf8MetadataPartBeforeCreatingAReview()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var textPlain = Multipart(ValidMetadata(), "text/plain; charset=utf-8");
        using var textResponse = await client.PostAsync(Route, textPlain);
        await AssertProblemAsync(textResponse, HttpStatusCode.BadRequest,
            "trip_review.invalid_input");

        using var utf16 = Multipart(
            Encoding.Unicode.GetBytes(ValidMetadata()),
            "application/json; charset=utf-16");
        using var utf16Response = await client.PostAsync(Route, utf16);
        await AssertProblemAsync(utf16Response, HttpStatusCode.BadRequest,
            "trip_review.invalid_input");

        using var malformedUtf8 = Multipart([0xC3, 0x28],
            "application/json; charset=utf-8");
        using var malformedResponse = await client.PostAsync(Route, malformedUtf8);
        await AssertProblemAsync(malformedResponse, HttpStatusCode.BadRequest,
            "trip_review.invalid_input");

        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task Post_AcceptsQuotedUtf8MetadataCharset()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(
            ValidMetadata(),
            "application/json; charset=\"utf-8\"");

        using var response = await client.PostAsync(Route, content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Post_MalformedMultipartFraming_ReturnsFeatureProblemDetails()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new StringContent("not-a-multipart-body", null, "multipart/form-data");
        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_review.invalid_input");
    }

    [Fact]
    public async Task Post_RejectsUnknownMetadataMember()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata("\"authorId\": 1,"));
        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_review.invalid_input");
    }

    [Theory]
    [InlineData("\"poiRatings\": null", "\"poiRatings\": []")]
    [InlineData("\"publishDisplayName\": null", "\"publishDisplayName\": false")]
    public async Task Post_RejectsExplicitNullForNonNullableOptionalMember(
        string replacement,
        string original)
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata().Replace(original, replacement,
            StringComparison.Ordinal));
        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_review.invalid_input");
    }

    [Fact]
    public async Task Post_RejectsMoreThanFiveFileParts()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata());
        for (var index = 0; index < 6; index++)
        {
            var file = new ByteArrayContent([1]);
            file.Headers.ContentType = new("image/jpeg");
            content.Add(file, "files", $"{index}.jpg");
        }

        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_review.invalid_input");
    }

    [Fact]
    public async Task Post_RejectsMetadataOverUtf8ByteLimit()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(new string('x', 64_001));
        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge,
            "trip_review.body_too_large");
    }

    [SqlServerFact]
    public async Task Post_AcceptsMetadataAtExactUtf8ByteLimit()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        var metadata = PadUtf8To(ValidMetadata(), 64_000);
        using var content = Multipart(metadata);

        using var response = await client.PostAsync(Route, content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Post_RejectsFileOverActualByteLimit()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata());
        var file = new ByteArrayContent(new byte[5_000_001]);
        file.Headers.ContentType = new("image/jpeg");
        content.Add(file, "files", "large.jpg");

        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge,
            "trip_review.body_too_large");
    }

    [SqlServerFact]
    public async Task Post_AcceptsFileAtExactActualByteLimit()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata());
        var file = new ByteArrayContent(new byte[5_000_000]);
        file.Headers.ContentType = new("image/jpeg");
        content.Add(file, "files", "exact.jpg");

        using var response = await client.PostAsync(Route, content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Post_RejectsTotalMultipartBodyOverLimit()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(ValidMetadata());
        var file = new ByteArrayContent(new byte[26_000_001]);
        file.Headers.ContentType = new("image/jpeg");
        content.Add(file, "files", "body-limit.jpg");

        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge,
            "trip_review.body_too_large");
    }

    [SqlServerFact]
    public async Task Post_ChunkedBodyCountsBytesAfterTheClosingBoundary()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var multipart = Multipart(ValidMetadata());
        var prefix = await multipart.ReadAsByteArrayAsync();
        var bytes = new byte[26_000_001];
        prefix.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)' ', prefix.Length, bytes.Length - prefix.Length);
        using var content = new UnknownLengthContent(bytes, multipart.Headers.ContentType!.ToString());

        using var response = await client.PostAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge,
            "trip_review.body_too_large");
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task Post_MaximumValidMultipartPayload_IsAcceptedBelowTotalLimit()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = Multipart(PadUtf8To(ValidMetadata(), 64_000));
        for (var index = 0; index < 5; index++)
        {
            var file = new ByteArrayContent(new byte[5_000_000]);
            file.Headers.ContentType = new("image/jpeg");
            content.Add(file, "files", $"exact-{index}.jpg");
        }

        using var response = await client.PostAsync(Route, content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Put_RejectsUnsupportedTextContentType()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new StringContent("{}", null, "text/plain");
        using var response = await client.PutAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType,
            "trip_review.unsupported_media_type");
    }

    [Fact]
    public async Task Put_RejectsUnsupportedMultipartContentType()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new MultipartFormDataContent
        {
            { new StringContent("{}"), "metadata" },
        };
        using var response = await client.PutAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType,
            "trip_review.unsupported_media_type");
    }

    [Fact]
    public async Task Put_MalformedMultipartContentType_IsRejectedByFeatureBeforeFormBinding()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new StringContent("{}", null, "multipart/form-data");
        using var response = await client.PutAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType,
            "trip_review.unsupported_media_type");
    }

    [Theory]
    [InlineData("\"poiRatings\": null,")]
    [InlineData("\"poiRatings\": [],")]
    [InlineData("\"routePacing\": \"wellPaced\",")]
    public async Task Put_RejectsCreateOnlyMemberRegardlessOfValue(string forbiddenMember)
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new StringContent(ValidEdit(forbiddenMember), null, "application/json");
        using var response = await client.PutAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest,
            "trip_review.invalid_edit_payload");
    }

    [Fact]
    public async Task Put_RejectsUnknownMember()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var content = new StringContent(ValidEdit("\"unknown\": true,"), null, "application/json");
        using var response = await client.PutAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest,
            "trip_review.invalid_edit_payload");
    }

    [Theory]
    [InlineData("\"overallRating\": null,", "\"overallRating\": 5,")]
    [InlineData("\"publishDisplayName\": \"false\",", "\"publishDisplayName\": false,")]
    [InlineData("\"version\": null", "\"version\": \"AAAAAAAAAAA=\"")]
    public async Task Put_RejectsWrongTypeOrNullAsInvalidInput(string replacement, string original)
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        var json = ValidEdit().Replace(original, replacement, StringComparison.Ordinal);
        using var content = new StringContent(json, null, "application/json");
        using var response = await client.PutAsync(Route, content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_review.invalid_input");
    }

    [Fact]
    public async Task Put_ValidationFailure_ReturnsFeatureProblemDetails()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        var json = ValidEdit().Replace(
            "\"overallRating\": 5,",
            "\"overallRating\": 0,",
            StringComparison.Ordinal);
        using var content = new StringContent(json, null, "application/json");

        using var response = await client.PutAsync(Route, content);

        await AssertValidationProblemAsync(response, "trip_review.invalid_input");
    }

    [Fact]
    public async Task PutStrictness_DoesNotChangeUnrelatedEndpointJsonBinding()
    {
        await using var factory = TransportFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var response = await client.PostAsJsonAsync("/api/v1/scheduling-requests", new
        {
            startAt = "2026-10-20T08:00:00+07:00",
            timeZoneId = "Asia/Ho_Chi_Minh",
            startLatitude = 16.0544m,
            startLongitude = 108.2022m,
            explorationLatitude = 16.0471m,
            explorationLongitude = 108.2068m,
            endPoiId = (long?)null,
            returnToStart = true,
            availableMinutes = 480,
            transportMode = "Motorbike",
            searchRadiusKm = 10m,
            budgetVnd = 800000m,
            mandatoryPoiIds = Array.Empty<long>(),
            restPreference = "None",
            unrelatedUnknownMember = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the missing Idempotency-Key remains the unrelated endpoint's only binding error");
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("trip_review.invalid_edit_payload");
        payload.Should().NotContain("unrelatedUnknownMember");
    }

    [SqlServerFact]
    public async Task Get_None_ReturnsOwnerContextAndUnavailablePoiCapabilityWithoutFabrication()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created.AddDays(1));
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("bookingId").GetInt64().Should().Be(1);
        root.GetProperty("existingReviewKind").GetString().Should().Be("none");
        root.GetProperty("review").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("canSubmit").GetBoolean().Should().BeTrue();
        root.GetProperty("submitUnavailableReason").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("routePacing").GetProperty("available").GetBoolean().Should().BeFalse();
        root.GetProperty("routePacing").GetProperty("reason").GetString()
            .Should().Be("routeContextUnavailable");
        root.GetProperty("cspRating").GetProperty("reason").GetString()
            .Should().Be("cspProvenanceUnavailable");
        root.GetProperty("poiRatings").GetProperty("reason").GetString()
            .Should().Be("visitEvidenceUnavailable");
        root.GetProperty("eligiblePois").GetArrayLength().Should().Be(0);
        root.GetProperty("editableFields").GetArrayLength().Should().Be(0);
    }

    [SqlServerFact]
    public async Task Get_NewAndLegacy_ReturnsTheFrozenDiscriminatedShapes()
    {
        await using var database = await SeedSqlAsync();
        await using (var context = database.CreateDbContext())
        {
            context.TripReviews.Add(TripReview.CreatePublished(
                2, 1, null, 2, 4, "Existing", "New review", RoutePacingFeedback.WellPaced, 4,
                false, "Owner Name", null, Created));
            await context.SaveChangesAsync();
        }
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',101,3,5,N'Legacy review');
            """);
        await using var factory = SqlFactory(database, Created.AddDays(1));
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var newResponse = await client.GetAsync("/api/v1/bookings/2/review");
        using var legacyResponse = await client.GetAsync("/api/v1/bookings/3/review");

        newResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var newBody = JsonDocument.Parse(await newResponse.Content.ReadAsStringAsync());
        newBody.RootElement.GetProperty("existingReviewKind").GetString().Should().Be("new");
        var newReview = newBody.RootElement.GetProperty("review");
        newReview.GetProperty("kind").GetString().Should().Be("new");
        newReview.GetProperty("routePacing").GetString().Should().Be("wellPaced");
        newReview.GetProperty("cspRating").GetInt32().Should().Be(4);
        newReview.GetProperty("poiRatings").GetArrayLength().Should().Be(0);
        newReview.GetProperty("media").GetArrayLength().Should().Be(0);
        AssertUtcTimestampSpelling(newReview, "createdAtUtc");
        AssertUtcTimestampSpelling(newReview, "editDeadlineUtc");
        AssertUtcTimestampSpelling(newReview, "updatedAtUtc");

        legacyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var legacyBody = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync());
        legacyBody.RootElement.GetProperty("existingReviewKind").GetString().Should().Be("legacy");
        var legacyReview = legacyBody.RootElement.GetProperty("review");
        legacyReview.GetProperty("kind").GetString().Should().Be("legacy");
        legacyReview.GetProperty("entries").GetArrayLength().Should().Be(1);
        legacyReview.TryGetProperty("title", out _).Should().BeFalse();
        AssertUtcTimestampSpelling(legacyReview.GetProperty("entries")[0], "createdAtUtc");

        var version = newReview.GetProperty("version").GetString();
        using var unchangedForbidden = new StringContent($$"""
            {
              "overallRating": 4,
              "title": "Existing",
              "content": "New review",
              "publishDisplayName": false,
              "version": "{{version}}",
              "routePacing": "wellPaced"
            }
            """, null, "application/json");
        using var invalidEdit = await client.PutAsync("/api/v1/bookings/2/review",
            unchangedForbidden);
        await AssertProblemAsync(invalidEdit, HttpStatusCode.BadRequest,
            "trip_review.invalid_edit_payload");
    }

    [SqlServerFact]
    public async Task Post_WithoutPhotos_CreatesDirectDtoAndDuplicateIsRecoverableByGet()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var created = await PostReviewAsync(client);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location!.OriginalString.Should().Be(Route);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        createdBody.RootElement.GetProperty("kind").GetString().Should().Be("new");
        createdBody.RootElement.GetProperty("media").GetArrayLength().Should().Be(0);
        createdBody.RootElement.GetProperty("routePacing").ValueKind.Should().Be(JsonValueKind.Null);
        createdBody.RootElement.GetProperty("cspRating").ValueKind.Should().Be(JsonValueKind.Null);
        createdBody.RootElement.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();

        using var duplicate = await PostReviewAsync(client);
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "trip_review.duplicate");

        using var recovered = await client.GetAsync(Route);
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
        using var recoveredBody = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
        recoveredBody.RootElement.GetProperty("existingReviewKind").GetString().Should().Be("new");

        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(1);
        (await verify.Reviews.CountAsync()).Should().Be(0,
            "canonical submission must not create a legacy mirror row");
    }

    [SqlServerFact]
    public async Task Post_WithPhoto_UsesMediaJournalAndReturnsOnlyPublicDeliveryData()
    {
        await using var database = await SeedSqlAsync();
        var storage = new SuccessfulStorage();
        await using var factory = SqlFactory(database, Created, services => ConfigureMedia(services, storage));
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var response = await PostReviewAsync(client, withPhoto: true);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("publicId");
        using var body = JsonDocument.Parse(payload);
        var media = body.RootElement.GetProperty("media");
        media.GetArrayLength().Should().Be(1);
        media[0].GetProperty("deliveryUrl").GetString()
            .Should().Be(SuccessfulStorage.DeliveryUrl);
        await using var verify = database.CreateDbContext();
        (await verify.TripReviewMedia.CountAsync()).Should().Be(1);
        (await verify.TripReviewMediaOperations.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Handoff_CreateGetEditGetAndAggregate_UsesFreshContextsWithoutDuplicateAdoption()
    {
        const long bookingId = 6;
        const string route = "/api/v1/bookings/6/review";
        await using var database = await SeedSqlAsync();

        string createdVersion;
        string createdAt;
        string editDeadline;
        long mediaId;
        await using (var createFactory = SqlFactory(database, Created,
            authenticationMode: ApiTestAuthenticationMode.JwtBearer))
        {
            using var createClient = createFactory.CreateJwtClient(
                TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
            using var initial = await createClient.GetAsync(route);
            initial.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var initialBody = JsonDocument.Parse(await initial.Content.ReadAsStringAsync()))
                initialBody.RootElement.GetProperty("existingReviewKind").GetString().Should().Be("none");

            using var created = await PostReviewAsync(createClient, bookingId, withPhoto: true,
                metadata: """
                    {
                      "overallRating": 5,
                      "title": "  Handoff Title  ",
                      "content": "  Handoff Content  ",
                      "poiRatings": [],
                      "routePacing": null,
                      "cspRating": null,
                      "publishDisplayName": false
                    }
                    """);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("title").GetString().Should().Be("Handoff Title");
            body.RootElement.GetProperty("content").GetString().Should().Be("Handoff Content");
            createdVersion = body.RootElement.GetProperty("version").GetString()!;
            createdAt = body.RootElement.GetProperty("createdAtUtc").GetString()!;
            editDeadline = body.RootElement.GetProperty("editDeadlineUtc").GetString()!;
            mediaId = body.RootElement.GetProperty("media")[0].GetProperty("mediaId").GetInt64();
        }

        await using (var recoveryFactory = SqlFactory(database, Created.AddMinutes(1),
            authenticationMode: ApiTestAuthenticationMode.JwtBearer))
        {
            using var recoveryClient = recoveryFactory.CreateJwtClient(
                TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
            using var duplicate = await PostReviewAsync(recoveryClient, bookingId, withPhoto: true);
            await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "trip_review.duplicate");

            using var recovered = await recoveryClient.GetAsync(route);
            recovered.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
            var review = body.RootElement.GetProperty("review");
            review.GetProperty("version").GetString().Should().Be(createdVersion);
            review.GetProperty("title").GetString().Should().Be("Handoff Title");
            review.GetProperty("content").GetString().Should().Be("Handoff Content");
            review.GetProperty("createdAtUtc").GetString().Should().Be(createdAt);
            review.GetProperty("editDeadlineUtc").GetString().Should().Be(editDeadline);
            review.GetProperty("routePacing").ValueKind.Should().Be(JsonValueKind.Null);
            review.GetProperty("cspRating").ValueKind.Should().Be(JsonValueKind.Null);
            review.GetProperty("poiRatings").GetArrayLength().Should().Be(0);
            var media = review.GetProperty("media");
            media.GetArrayLength().Should().Be(1);
            media[0].GetProperty("mediaId").GetInt64().Should().Be(mediaId);
            media[0].GetProperty("deliveryUrl").GetString().Should().Be(SuccessfulStorage.DeliveryUrl);
            review.GetProperty("publicDisplayName").GetString().Should().Be("O. N.");
            review.GetProperty("publicDisplayName").GetString().Should().NotBe("Owner Name");

            await using (var createdAggregateContext = database.CreateDbContext())
            {
                var createdAggregate = await TripReviewAggregateReader.ReadTourAsync(
                    createdAggregateContext,
                    1,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                createdAggregate.Value.Should().Be(new TripReviewAggregateDto(5m, 1));
            }

            using var editContent = JsonContent.Create(new
            {
                overallRating = 3,
                title = "Edited handoff title",
                content = "Edited handoff content",
                publishDisplayName = true,
                version = createdVersion,
            });
            using var edited = await recoveryClient.PutAsync(route, editContent);
            edited.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        string editedVersion;
        await using (var readFactory = SqlFactory(database, Created.AddMinutes(2),
            authenticationMode: ApiTestAuthenticationMode.JwtBearer))
        {
            using var readClient = readFactory.CreateJwtClient(
                TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
            using var persisted = await readClient.GetAsync(route);
            persisted.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await persisted.Content.ReadAsStringAsync());
            var review = body.RootElement.GetProperty("review");
            review.GetProperty("overallRating").GetInt32().Should().Be(3);
            review.GetProperty("title").GetString().Should().Be("Edited handoff title");
            review.GetProperty("content").GetString().Should().Be("Edited handoff content");
            review.GetProperty("publishDisplayName").GetBoolean().Should().BeTrue();
            review.GetProperty("publicDisplayName").GetString().Should().Be("Owner Name");
            editedVersion = review.GetProperty("version").GetString()!;
            editedVersion.Should().NotBe(createdVersion);
            review.GetProperty("createdAtUtc").GetString().Should().Be(createdAt);
            review.GetProperty("editDeadlineUtc").GetString().Should().Be(editDeadline);
            review.GetProperty("routePacing").ValueKind.Should().Be(JsonValueKind.Null);
            review.GetProperty("cspRating").ValueKind.Should().Be(JsonValueKind.Null);
            review.GetProperty("poiRatings").GetArrayLength().Should().Be(0);
            var media = review.GetProperty("media");
            media[0].GetProperty("mediaId").GetInt64().Should().Be(mediaId);
            media[0].GetProperty("deliveryUrl").GetString()
                .Should().Be(SuccessfulStorage.DeliveryUrl);
        }

        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(1);
        (await verify.TripReviewMedia.CountAsync()).Should().Be(1);
        (await verify.TripReviewMediaOperations.CountAsync()).Should().Be(1);
        var aggregate = await TripReviewAggregateReader.ReadTourAsync(
            verify,
            1,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        aggregate.Value.Should().Be(new TripReviewAggregateDto(3m, 1));
    }

    [SqlServerFact]
    public async Task Post_MapsLegacyConflictIncompleteForeignInactiveAndStorageFailuresSafely()
    {
        await using var database = await SeedSqlAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',101,3,4,N'First'),(1,'POI',102,3,5,N'Second');
            """);
        await using var factory = SqlFactory(database, Created);
        using var owner = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var inactive = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        using var legacyConflict = await PostReviewAsync(owner, bookingId: 3);
        await AssertProblemAsync(legacyConflict, HttpStatusCode.Conflict,
            "trip_review.legacy_conflict");
        using var incomplete = await PostReviewAsync(owner, bookingId: 4);
        await AssertProblemAsync(incomplete, HttpStatusCode.Conflict,
            "trip_review.booking_not_completed");
        using var foreign = await owner.GetAsync("/api/v1/bookings/5/review");
        await AssertProblemAsync(foreign, HttpStatusCode.NotFound,
            "trip_review.booking_not_found");
        using var inactiveResponse = await inactive.GetAsync(Route);
        await AssertProblemAsync(inactiveResponse, HttpStatusCode.Forbidden,
            "trip_review.forbidden");

        await using var storageDatabase = await SeedSqlAsync();
        await using var storageFactory = SqlFactory(storageDatabase, Created,
            services => ConfigureMedia(services, new FailingStorage()));
        using var storageClient = storageFactory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var storageFailure = await PostReviewAsync(storageClient, withPhoto: true);
        var storagePayload = await storageFailure.Content.ReadAsStringAsync();
        await AssertProblemPayloadAsync(storageFailure, storagePayload,
            HttpStatusCode.ServiceUnavailable, "trip_review.storage_unavailable");
        storagePayload.Should().NotContainEquivalentOf("publicId");
        storagePayload.Should().NotContainEquivalentOf("cloudinary");
        storagePayload.Should().NotContainEquivalentOf("sql");
        await using var storageVerify = storageDatabase.CreateDbContext();
        (await storageVerify.TripReviews.CountAsync()).Should().Be(0);
        (await storageVerify.TripReviewMedia.CountAsync()).Should().Be(0);
        (await storageVerify.TripReviewMediaOperations.SingleAsync()).State
            .Should().Be(TripReviewMediaOperation.CleanupPending);
    }

    [SqlServerFact]
    public async Task Post_MapsIndependentUnavailableCapabilityInputs()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var pacing = await PostReviewAsync(client, bookingId: 1, metadata: $$"""
            {
              "overallRating": 5,
              "title": "Title",
              "content": "Content",
              "poiRatings": [],
              "routePacing": "wellPaced",
              "cspRating": null,
              "publishDisplayName": false
            }
            """);
        await AssertProblemAsync(pacing, HttpStatusCode.BadRequest,
            "trip_review.route_pacing_unavailable");

        using var csp = await PostReviewAsync(client, bookingId: 2, metadata: ValidMetadata()
            .Replace("\"cspRating\": null", "\"cspRating\": 5", StringComparison.Ordinal));
        await AssertProblemAsync(csp, HttpStatusCode.BadRequest,
            "trip_review.csp_ineligible");

        using var poi = await PostReviewAsync(client, bookingId: 3, metadata: ValidMetadata()
            .Replace("\"poiRatings\": []", "\"poiRatings\": [{\"poiId\": 101, \"rating\": 5}]",
                StringComparison.Ordinal));
        await AssertProblemAsync(poi, HttpStatusCode.BadRequest,
            "trip_review.poi_unavailable");

        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task C4_OwnedCspProvenanceAcceptsCspWhileRouteAndPoiRemainUnavailable()
    {
        const string route = "/api/v1/bookings/7/review";
        await using var database = await SeedSqlAsync();
        await using (var factory = SqlFactory(database, Created,
            authenticationMode: ApiTestAuthenticationMode.JwtBearer))
        {
            using var client = factory.CreateJwtClient(
                TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
            using var contextResponse = await client.GetAsync(route);
            contextResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var contextBody = JsonDocument.Parse(await contextResponse.Content.ReadAsStringAsync()))
            {
                contextBody.RootElement.GetProperty("cspRating").GetProperty("available").GetBoolean()
                    .Should().BeTrue();
                contextBody.RootElement.GetProperty("routePacing").GetProperty("available").GetBoolean()
                    .Should().BeFalse();
                contextBody.RootElement.GetProperty("poiRatings").GetProperty("available").GetBoolean()
                    .Should().BeFalse();
            }

            using var created = await PostReviewAsync(client, 7, metadata: ValidMetadata()
                .Replace("\"cspRating\": null", "\"cspRating\": 4", StringComparison.Ordinal));
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            createdBody.RootElement.GetProperty("cspRating").GetInt32().Should().Be(4);
            createdBody.RootElement.GetProperty("routePacing").ValueKind.Should().Be(JsonValueKind.Null);
            createdBody.RootElement.GetProperty("poiRatings").GetArrayLength().Should().Be(0);
        }

        await using var readFactory = SqlFactory(database, Created.AddMinutes(1),
            authenticationMode: ApiTestAuthenticationMode.JwtBearer);
        using var readClient = readFactory.CreateJwtClient(
            TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
        using var persisted = await readClient.GetAsync(route);
        persisted.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await persisted.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("review").GetProperty("cspRating").GetInt32().Should().Be(4);
    }

    [SqlServerFact]
    public async Task Put_ValidEditReturnsCurrentVersion_AndOriginalVersionBecomesStale()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var created = await PostReviewAsync(client);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var originalVersion = createdBody.RootElement.GetProperty("version").GetString()!;

        using var edited = await PutReviewAsync(client, originalVersion, "Edited Title");

        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        using var editedBody = JsonDocument.Parse(await edited.Content.ReadAsStringAsync());
        editedBody.RootElement.GetProperty("title").GetString().Should().Be("Edited Title");
        editedBody.RootElement.GetProperty("version").GetString().Should().NotBe(originalVersion);

        using var stale = await PutReviewAsync(client, originalVersion, "Stale Edit");
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "trip_review.stale_version");
    }

    [SqlServerFact]
    public async Task Put_RejectsEveryImmutableOrCreateOnlyMemberWithoutWriting()
    {
        await using var database = await SeedSqlAsync();
        await using var factory = SqlFactory(database, Created);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var created = await PostReviewAsync(client);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var version = createdBody.RootElement.GetProperty("version").GetString()!;
        var forbiddenMembers = new[]
        {
            "\"poiRatings\": null,",
            "\"poiRatings\": [],",
            "\"routePacing\": null,",
            "\"cspRating\": null,",
            "\"media\": [],",
            "\"files\": [],",
            "\"newFiles\": [],",
            "\"retainedMediaIds\": [],",
            $"\"createdAtUtc\": \"{Created:O}\",",
            "\"publicDisplayName\": \"Owner Name\",",
        };

        foreach (var member in forbiddenMembers)
        {
            using var content = new StringContent(
                ValidEdit(member).Replace("AAAAAAAAAAA=", version, StringComparison.Ordinal),
                null,
                "application/json");
            using var response = await client.PutAsync(Route, content);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest,
                "trip_review.invalid_edit_payload");
        }

        await using var verify = database.CreateDbContext();
        var review = await verify.TripReviews.AsNoTracking().SingleAsync();
        review.Title.Should().Be("Title");
        review.Content.Should().Be("Content");
        review.OverallRating.Should().Be(5);
    }

    [SqlServerFact]
    public async Task Put_AtExactDeadline_IsExpiredAndPreservesTheReview()
    {
        await using var database = await SeedSqlAsync();
        string version;
        await using (var createFactory = SqlFactory(database, Created))
        {
            using var createClient = createFactory.CreateAuthenticatedClient(1, UserRole.Traveler);
            using var created = await PostReviewAsync(createClient);
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            version = body.RootElement.GetProperty("version").GetString()!;
        }

        await using var editFactory = SqlFactory(database, Created.AddDays(TripReview.EditWindowDays));
        using var editClient = editFactory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var response = await PutReviewAsync(editClient, version, "Too Late");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "trip_review.edit_expired");
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.AsNoTracking().SingleAsync()).Title.Should().Be("Title");
    }

    [SqlServerFact]
    public async Task Post_RequestCancellation_ReachesHandlerAndPersistsNoReview()
    {
        await using var database = await SeedSqlAsync();
        var media = new CancellationMedia();
        await using var factory = SqlFactory(database, Created, services =>
        {
            services.RemoveAll<IReviewMediaCoordinator>();
            services.AddSingleton<IReviewMediaCoordinator>(media);
        });
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var send = PostReviewAsync(client, withPhoto: true, cancellationToken: cancellation.Token);
        await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await FluentActions.Invoking(async () => await send)
            .Should().ThrowAsync<OperationCanceledException>();
        await media.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(0);
    }

    private static HttpRequestMessage Request(string method) => method switch
    {
        "GET" => new(HttpMethod.Get, Route),
        "POST" => new(HttpMethod.Post, Route)
        {
            Content = new MultipartFormDataContent
            {
                { new StringContent("not-json"), "metadata" },
            },
        },
        "PUT" => new(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new { forbidden = true }),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static TripMateApiFactory SqlFactory(
        SqlServerTestDatabase database,
        DateTimeOffset now,
        Action<IServiceCollection>? configureServices = null,
        ApiTestAuthenticationMode authenticationMode = ApiTestAuthenticationMode.HeaderStub)
    {
        return new(
            authenticationMode: authenticationMode,
            sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedClock(now),
            configureTestServices: services =>
            {
                ConfigureMedia(services, new SuccessfulStorage());
                services.RemoveAll<IReviewContentModerator>();
                services.AddSingleton<IReviewContentModerator, AcceptedModerator>();
                configureServices?.Invoke(services);
            });
    }

    private static TripMateApiFactory TransportFactory() => new(
        configureTestServices: services =>
            services.AddSingleton<IAuthorizationHandler, AllowActiveTravelerHandler>());

    private static async Task<SqlServerTestDatabase> SeedSqlAsync()
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteNonQueryAsync("""
                INSERT dbo.Users(role,status,email,full_name)
                VALUES('Traveler','Active',N'owner@test.invalid',N'Owner Name'),
                      ('Traveler','Inactive',N'inactive@test.invalid',N'Inactive User'),
                      ('Traveler','Active',N'foreign@test.invalid',N'Foreign User'),
                      ('TourOperator','Active',N'operator@test.invalid',N'Operator User');
                INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
                VALUES(4,N'TM79 Handoff Operator',N'TM79-HANDOFF-TAX',N'TM79-HANDOFF-LICENSE','Approved');
                INSERT commerce.Tours(operator_user_id,title,base_price,duration_days,status)
                VALUES(4,N'TM79 Handoff Tour',0,1,'Approved');
                INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity,status)
                VALUES(1,'2026-09-01','2026-09-02',10,'Completed');
                INSERT planning.Itineraries(traveler_user_id,source_type,title)
                VALUES(1,'Manual',N'First'),(1,'Manual',N'Second'),(1,'Manual',N'Third'),
                      (1,'Manual',N'Incomplete'),(3,'Manual',N'Foreign');
                INSERT planning.SchedulingRequests(traveler_user_id,idempotency_key,request_hash,start_at,
                    start_latitude,start_longitude,destination_latitude,destination_longitude,
                    available_minutes,search_radius_km)
                VALUES(1,NEWID(),REPLICATE('a',64),'2026-09-01',16,108,16,108,60,2);
                INSERT planning.Itineraries(traveler_user_id,source_type,title,scheduling_request_id)
                VALUES(1,'CSPGenerated',N'CSP Review Context',1);
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-HTTP-1',1,1,0,0,'Completed'),
                      ('TM79-HTTP-2',1,2,0,0,'Completed'),
                      ('TM79-HTTP-3',1,3,0,0,'Completed'),
                      ('TM79-HTTP-4',1,4,0,0,'Confirmed'),
                      ('TM79-HTTP-5',3,5,0,0,'Completed');
                INSERT commerce.Bookings(booking_code,traveler_user_id,tour_schedule_id,unit_price,total_amount,status)
                VALUES('TM79-HTTP-6',1,1,0,0,'Completed');
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-HTTP-7',1,6,0,0,'Completed');
                """);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> PostReviewAsync(
        HttpClient client,
        long bookingId = 1,
        bool withPhoto = false,
        string? metadata = null,
        CancellationToken cancellationToken = default)
    {
        using var content = Multipart(metadata ?? ValidMetadata());
        if (withPhoto)
        {
            var file = new ByteArrayContent([1, 2, 3, 4]);
            file.Headers.ContentType = new("image/jpeg");
            content.Add(file, "files", "review.jpg");
        }
        return await client.PostAsync($"/api/v1/bookings/{bookingId}/review", content, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PutReviewAsync(
        HttpClient client,
        string version,
        string title)
    {
        using var content = JsonContent.Create(new
        {
            overallRating = 4,
            title,
            content = "Edited Content",
            publishDisplayName = true,
            version,
        });
        return await client.PutAsync(Route, content);
    }

    private static void ConfigureMedia(IServiceCollection services, IReviewMediaStorage storage)
    {
        services.RemoveAll<IReviewImageInspector>();
        services.RemoveAll<IReviewMediaStorage>();
        services.AddSingleton<IReviewImageInspector, AcceptingImageInspector>();
        services.AddSingleton(storage);
    }

    private static MultipartFormDataContent Multipart(string metadata)
        => Multipart(Encoding.UTF8.GetBytes(metadata), "application/json; charset=utf-8");

    private static MultipartFormDataContent Multipart(string metadata, string contentType)
        => Multipart(Encoding.UTF8.GetBytes(metadata), contentType);

    private static MultipartFormDataContent Multipart(byte[] metadata, string contentType)
    {
        var content = new MultipartFormDataContent();
        var json = new ByteArrayContent(metadata);
        json.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        content.Add(json, "metadata");
        return content;
    }

    private static string ValidMetadata(string extra = "") => $$"""
        {
          {{extra}}
          "overallRating": 5,
          "title": "Title",
          "content": "Content",
          "poiRatings": [],
          "routePacing": null,
          "cspRating": null,
          "publishDisplayName": false
        }
        """;

    private static string ValidEdit(string extra = "") => $$"""
        {
          {{extra}}
          "overallRating": 5,
          "title": "Title",
          "content": "Content",
          "publishDisplayName": false,
          "version": "AAAAAAAAAAA="
        }
        """;

    private static string PadUtf8To(string value, int byteLength)
    {
        var currentLength = Encoding.UTF8.GetByteCount(value);
        if (currentLength > byteLength)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        return value + new string(' ', byteLength - currentLength);
    }

    private static void AssertUtcTimestampSpelling(JsonElement value, string propertyName)
    {
        var timestamp = value.GetProperty(propertyName).GetString();
        timestamp.Should().EndWith("Z");
        timestamp.Should().NotContain("+00:00");
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string errorCode)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, "the response body was {0}", payload);
        using var body = JsonDocument.Parse(payload);
        body.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    private static async Task AssertValidationProblemAsync(
        HttpResponseMessage response,
        string errorCode)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the response body was {0}", payload);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(payload);
        body.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        body.RootElement.GetProperty("errors").ValueKind.Should().Be(JsonValueKind.Object);
    }

    private static Task AssertProblemPayloadAsync(
        HttpResponseMessage response,
        string payload,
        HttpStatusCode status,
        string errorCode)
    {
        response.StatusCode.Should().Be(status, "the response body was {0}", payload);
        using var body = JsonDocument.Parse(payload);
        body.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        return Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class AcceptedModerator : IReviewContentModerator
    {
        public string ActivePolicyVersion => ReviewContentPolicy.ActiveVersion;

        public Task<ReviewContentModerationResult> ScreenAsync(
            ReviewText text,
            CancellationToken cancellationToken) =>
            Task.FromResult(ReviewContentModerationResult.Accepted(ActivePolicyVersion));
    }

    private sealed class AllowActiveTravelerHandler : AuthorizationHandler<ActiveTravelerRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            ActiveTravelerRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptingImageInspector : IReviewImageInspector
    {
        public async Task<ReviewImageInspection> InspectAsync(
            ReviewImageSource source,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            await source.Content.CopyToAsync(output, cancellationToken);
            return new(new(output.ToArray(), "image/jpeg", ".jpg", 1, 1), null);
        }
    }

    private sealed class SuccessfulStorage : IReviewMediaStorage
    {
        public const string DeliveryUrl = "https://res.cloudinary.test/tripmate/reviews/review.jpg";

        public Task<ReviewMediaStorageResult> UploadAsync(
            string publicId,
            InspectedReviewImage image,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewMediaStorageResult(
                ReviewMediaStorageOutcome.Success,
                DeliveryUrl,
                image.Bytes.LongLength));

        public Task<ReviewMediaStorageResult> ProbeAsync(
            string publicId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewMediaStorageResult(
                ReviewMediaStorageOutcome.Success,
                DeliveryUrl,
                4));

        public Task<ReviewMediaStorageResult> DestroyAsync(
            string publicId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success));
    }

    private sealed class FailingStorage : IReviewMediaStorage
    {
        public Task<ReviewMediaStorageResult> UploadAsync(
            string publicId,
            InspectedReviewImage image,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.TransientFailure));

        public Task<ReviewMediaStorageResult> ProbeAsync(
            string publicId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.AlreadyAbsent));

        public Task<ReviewMediaStorageResult> DestroyAsync(
            string publicId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.AlreadyAbsent));
    }

    private sealed class CancellationMedia : IReviewMediaCoordinator
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PreparedReviewMedia>> PrepareAsync(
            long bookingId,
            long travelerId,
            IReadOnlyList<ReviewImageSource> images,
            CancellationToken ct)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("The cancellation gate unexpectedly completed.");
            }
            finally
            {
                if (ct.IsCancellationRequested)
                    CancellationObserved.TrySetResult();
            }
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _content;

        public UnknownLengthContent(byte[] content, string contentType)
        {
            _content = content;
            Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content, 0, _content.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

    }
}