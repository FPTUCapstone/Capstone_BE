using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SkiaSharp;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Reviews;

internal sealed class Task13CloudinaryHandoffFactAttribute : FactAttribute
{
    public Task13CloudinaryHandoffFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("TRIPMATE_CLOUDINARY_HANDOFF_SMOKE"),
            "1",
            StringComparison.Ordinal))
        {
            Skip = "Set TRIPMATE_CLOUDINARY_HANDOFF_SMOKE=1 to opt in to the real HTTP/SQL/provider smoke.";
            return;
        }

        string[] required =
        [
            SqlServerTestDatabase.ConnectionStringEnvironmentVariable,
            "Cloudinary__CloudName",
            "Cloudinary__ApiKey",
            "Cloudinary__ApiSecret",
        ];
        if (required.Any(name => string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Isolated SQL and development Cloudinary configuration are required for this smoke.";
        }
    }
}

[Collection(nameof(TripMateApiFactory))]
public sealed class Task13CloudinaryHandoffSmokeTests
{
    private const string Route = "/api/v1/bookings/1/review";
    private static readonly DateTimeOffset Now =
        new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Task13CloudinaryHandoffFact]
    [Trait("Category", "CloudinaryHandoffSmoke")]
    public async Task RealProvider_HttpSqlPersistenceDeliveryAndExactCleanup()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        var publicIds = new List<string>();

        try
        {
            string deliveryUrl;
            long mediaId;
            await using (var createFactory = Factory(database))
            {
                using var client = createFactory.CreateJwtClient(
                    TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
                using var response = await PostAsync(client);
                var payload = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.Created,
                    "the response body was {0}", payload);
                using var body = JsonDocument.Parse(payload);
                var media = body.RootElement.GetProperty("media");
                media.GetArrayLength().Should().Be(1);
                mediaId = media[0].GetProperty("mediaId").GetInt64();
                deliveryUrl = media[0].GetProperty("deliveryUrl").GetString()!;
                Uri.TryCreate(deliveryUrl, UriKind.Absolute, out var uri).Should().BeTrue();
                uri!.Scheme.Should().Be(Uri.UriSchemeHttps);
                uri.UserInfo.Should().BeEmpty();
            }

            await using (var sql = database.CreateDbContext())
            {
                (await sql.TripReviews.CountAsync()).Should().Be(1);
                var persisted = await sql.TripReviewMedia.AsNoTracking()
                    .Include(item => item.Operation)
                    .SingleAsync();
                persisted.Id.Should().Be(mediaId);
                persisted.Operation.State.Should().Be(TripReviewMediaOperation.Adopted);
                persisted.Operation.DeliveryUrl.Should().Be(deliveryUrl);
                string.IsNullOrWhiteSpace(persisted.Operation.PublicId).Should().BeFalse();
                (await sql.TripReviewMediaOperations.CountAsync()).Should().Be(1);
                publicIds.Add(persisted.Operation.PublicId);
            }

            await using (var readFactory = Factory(database))
            {
                using var client = readFactory.CreateJwtClient(
                    TestJwtTokenFactory.CreateValid(1, UserRole.Traveler));
                using var response = await client.GetAsync(Route);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                var persistedMedia = body.RootElement.GetProperty("review")
                    .GetProperty("media");
                persistedMedia.GetArrayLength().Should().Be(1);
                persistedMedia[0].GetProperty("mediaId").GetInt64().Should().Be(mediaId);
                persistedMedia[0].GetProperty("deliveryUrl").GetString()
                    .Should().Be(deliveryUrl);
            }

            using var deliveryClient = new HttpClient();
            using var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var delivery = await ReadDeliveryAsync(
                deliveryClient,
                deliveryUrl,
                deliveryTimeout.Token);
            delivery.IsSuccessStatusCode.Should().BeTrue();
            delivery.Content.Headers.ContentType?.MediaType.Should().StartWith("image/");
            var deliveredBytes = await delivery.Content.ReadAsByteArrayAsync();
            deliveredBytes.Should().NotBeEmpty();
            using var deliveredData = SKData.CreateCopy(deliveredBytes);
            using var deliveredCodec = SKCodec.Create(deliveredData);
            deliveredCodec.Should().NotBeNull();
            deliveredCodec!.Info.Width.Should().BeGreaterThan(0);
            deliveredCodec.Info.Height.Should().BeGreaterThan(0);
        }
        finally
        {
            if (publicIds.Count == 0)
            {
                await using var sql = database.CreateDbContext();
                publicIds.AddRange(await sql.TripReviewMediaOperations.AsNoTracking()
                    .Select(operation => operation.PublicId)
                    .ToListAsync());
            }

            await using var cleanupFactory = Factory(database);
            using var scope = cleanupFactory.Services.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IReviewMediaStorage>();
            foreach (var publicId in publicIds.Distinct(StringComparer.Ordinal))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                (await DestroyAndConfirmAbsentAsync(storage, publicId, timeout.Token))
                    .Should().BeTrue("the provider smoke must leave zero operation-owned assets");
            }
        }
    }

    private static TripMateApiFactory Factory(SqlServerTestDatabase database) => new(
        authenticationMode: ApiTestAuthenticationMode.JwtBearer,
        sqlServerConnectionString: database.ConnectionString,
        dateTimeProviderFactory: _ => new FixedClock(Now));

    private static async Task SeedAsync(SqlServerTestDatabase database) =>
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role,status,email,full_name)
            VALUES('Traveler','Active',N'tm79-cloudinary-smoke@test.invalid',N'Provider Smoke');
            INSERT planning.Itineraries(traveler_user_id,source_type,title)
            VALUES(1,'Manual',N'TM79 Provider Smoke');
            INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
            VALUES('TM79-CLOUDINARY-SMOKE',1,1,0,0,'Completed');
            """);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent();
        var metadata = new ByteArrayContent(Encoding.UTF8.GetBytes("""
            {
              "overallRating": 5,
              "title": "Provider smoke",
              "content": "Task 13 provider handoff",
              "poiRatings": [],
              "routePacing": null,
              "cspRating": null,
              "publishDisplayName": false
            }
            """));
        metadata.Headers.ContentType = new("application/json");
        content.Add(metadata, "metadata");
        var file = new ByteArrayContent(SyntheticPng());
        file.Headers.ContentType = new("image/png");
        content.Add(file, "files", "task13-smoke.png");
        return await client.PostAsync(Route, content);
    }

    private static byte[] SyntheticPng()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(new SKColor(18, 151, 137, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task<HttpResponseMessage> ReadDeliveryAsync(
        HttpClient client,
        string deliveryUrl,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await client.GetAsync(deliveryUrl, cancellationToken);
            if (response.IsSuccessStatusCode || attempt == 9)
                return response;
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException("The delivery retry loop did not return.");
    }

    private static async Task<bool> DestroyAndConfirmAbsentAsync(
        IReviewMediaStorage storage,
        string publicId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await storage.DestroyAsync(publicId, cancellationToken);
            if ((await storage.ProbeAsync(publicId, cancellationToken)).Outcome
                == ReviewMediaStorageOutcome.AlreadyAbsent)
            {
                return true;
            }

            if (attempt < 9)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}