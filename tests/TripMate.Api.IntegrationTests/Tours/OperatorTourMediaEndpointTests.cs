using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorTourMediaEndpointTests
{
    [Fact]
    public async Task List_WithoutAuthentication_Returns401()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/operator/tours/42/media");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_WithTravelerRole_Returns403()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/operator/tours/42/media");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Upload_WithoutValidIdempotencyKey_Returns400ProblemDetails()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);
        using var form = new MultipartFormDataContent();

        var response = await client.PostAsync("/api/v1/operator/tours/42/media", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Swagger_DescribesOperatorMediaRoutesAndMultipartUpload()
    {
        using var factory = new TripMateApiFactory(environmentName: "Development");
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");
        var mediaPath = paths.GetProperty("/api/v1/operator/tours/{tourId}/media");

        mediaPath.TryGetProperty("get", out _).Should().BeTrue();
        var post = mediaPath.GetProperty("post");
        post.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("multipart/form-data", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/operator/tours/{tourId}/media/{mediaId}")
            .TryGetProperty("delete", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/operator/tours/{tourId}/media/order")
            .TryGetProperty("put", out _).Should().BeTrue();

        var parameters = post.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, parameter => parameter);
        parameters.Keys.Should().Contain("Idempotency-Key");
        parameters["Idempotency-Key"].GetProperty("required").GetBoolean().Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("201", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("400", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("413", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("503", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/operator/tours/{tourId}/media/{mediaId}")
            .TryGetProperty("patch", out _).Should().BeTrue();

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var uploadRequestSchema = post.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        var uploadProperties = uploadRequestSchema.TryGetProperty("$ref", out var uploadReference)
            ? schemas.GetProperty(uploadReference.GetString()!.Split('/')[^1]).GetProperty("properties")
            : uploadRequestSchema.GetProperty("properties");
        GetPropertyIgnoreCase(uploadProperties, "file").GetProperty("format").GetString().Should().Be("binary");
        var reorderOperation = paths.GetProperty("/api/v1/operator/tours/{tourId}/media/order")
            .GetProperty("put");
        var reorderSchema = reorderOperation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var reorderProperties = schemas.GetProperty(
            reorderSchema.GetProperty("$ref").GetString()!.Split('/')[^1]).GetProperty("properties");
        GetPropertyIgnoreCase(reorderProperties, "mediaIds").GetProperty("items").GetProperty("format")
            .GetString().Should().Be("int64");
        var mediaSchema = schemas.EnumerateObject().Single(property =>
            property.Value.TryGetProperty("properties", out var candidateProperties) &&
            candidateProperties.EnumerateObject().Any(mediaProperty =>
                string.Equals(mediaProperty.Name, "tourMediaId", StringComparison.OrdinalIgnoreCase))).Value;
        var mediaProperties = mediaSchema.GetProperty("properties");
        GetPropertyIgnoreCase(mediaProperties, "tourMediaId").GetProperty("format").GetString().Should().Be("int64");
        mediaProperties.EnumerateObject().Any(property =>
            string.Equals(property.Name, "createdAtUtc", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
        mediaProperties.EnumerateObject().Any(property =>
            string.Equals(property.Name, "updatedAtUtc", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
    }

    [Fact]
    public async Task OwnedOperator_CanUploadListEditReorderAndSoftDeleteMedia()
    {
        var inspector = new FakeImageInspector();
        var storage = new FakeMediaStorage();
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<ITourMediaImageInspector>();
            services.RemoveAll<ITourMediaStorage>();
            services.AddSingleton<ITourMediaImageInspector>(inspector);
            services.AddSingleton<ITourMediaStorage>(storage);
        });
        var actorAndTour = await SeedOwnedTourAsync(factory);
        using var client = factory.CreateAuthenticatedClient(actorAndTour.ActorId, UserRole.TourOperator);
        const string collection = "/api/v1/operator/tours/42/media";

        using (var initiallyEmpty = await client.GetAsync(collection))
        {
            initiallyEmpty.StatusCode.Should().Be(HttpStatusCode.OK);
            using var initiallyEmptyJson = JsonDocument.Parse(await initiallyEmpty.Content.ReadAsStringAsync());
            initiallyEmptyJson.RootElement.GetArrayLength().Should().Be(0);
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Accessible tour image"), "AltText");
        form.Add(new StringContent("true"), "IsPrimary");
        var image = new ByteArrayContent([1, 2, 3]);
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(image, "File", "tour.png");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, collection) { Content = form };
        uploadRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var upload = await client.SendAsync(uploadRequest);
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        upload.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var uploadJson = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var mediaId = uploadJson.RootElement.GetProperty("tourMediaId").GetInt64();
        uploadJson.RootElement.GetProperty("deliveryUrl").GetString().Should().StartWith("https://");
        uploadJson.RootElement.TryGetProperty("cloudinaryPublicId", out _).Should().BeFalse();

        using var list = await client.GetAsync(collection);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listJson.RootElement.GetArrayLength().Should().Be(1);
        listJson.RootElement[0].GetProperty("tourMediaId").GetInt64().Should().Be(mediaId);
        listJson.RootElement[0].TryGetProperty("cloudinaryPublicId", out _).Should().BeFalse();

        using var update = await client.PatchAsJsonAsync(
            $"{collection}/{mediaId}",
            new { caption = "Updated caption", altText = "Updated accessible text" });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        using var reorder = await client.PutAsJsonAsync(
            $"{collection}/order",
            new { mediaIds = new[] { mediaId }, primaryMediaId = mediaId });
        reorder.StatusCode.Should().Be(HttpStatusCode.OK);

        using var delete = await client.DeleteAsync($"{collection}/{mediaId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"{collection}/{mediaId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var emptyList = await client.GetAsync(collection);
        using var emptyJson = JsonDocument.Parse(await emptyList.Content.ReadAsStringAsync());
        emptyJson.RootElement.GetArrayLength().Should().Be(0);

        storage.UploadCount.Should().Be(1);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.TourMedia.IgnoreQueryFilters().CountAsync()).Should().Be(1);
            (await db.TourMedia.IgnoreQueryFilters().SingleAsync()).LifecycleStatus.Should().Be(TourMediaLifecycleStatus.Deleted);
            (await db.TourMediaCleanupOutbox.CountAsync()).Should().Be(1);
            (await db.AuditLogs.CountAsync()).Should().Be(4);
            return true;
        });
    }

    [Fact]
    public async Task NonOwnerOperator_GetsNonDisclosing404ForOwnedMediaCollection()
    {
        using var factory = new TripMateApiFactory();
        await SeedOwnedTourAsync(factory);
        await SeedSecondOperatorAsync(factory, 999);
        using var client = factory.CreateAuthenticatedClient(999, UserRole.TourOperator);

        using var response = await client.GetAsync("/api/v1/operator/tours/42/media");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be("tour_media.tour_not_found");
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.Administrator)]
    public async Task NonOperatorRole_CannotAccessOperatorMedia(UserRole role)
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(700, role);

        using var response = await client.GetAsync("/api/v1/operator/tours/42/media");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Upload_WhenProviderUnavailable_ReturnsRedacted503ProblemDetails()
    {
        var inspector = new FakeImageInspector();
        var storage = new FakeMediaStorage { FailUpload = true };
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<ITourMediaImageInspector>();
            services.RemoveAll<ITourMediaStorage>();
            services.AddSingleton<ITourMediaImageInspector>(inspector);
            services.AddSingleton<ITourMediaStorage>(storage);
        });
        var seed = await SeedOwnedTourAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.ActorId, UserRole.TourOperator);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Accessible image"), "AltText");
        form.Add(new ByteArrayContent([1, 2, 3]), "File", "tour.png");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/operator/tours/42/media")
        { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("tour_media.provider_unavailable");
        body.Should().NotContain("provider-private-body");
        body.Should().NotContain("api-secret");
    }

    [Fact]
    public async Task Upload_WhenTourIsPending_Returns409BeforeCallingProvider()
    {
        var inspector = new FakeImageInspector();
        var storage = new FakeMediaStorage();
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<ITourMediaImageInspector>();
            services.RemoveAll<ITourMediaStorage>();
            services.AddSingleton<ITourMediaImageInspector>(inspector);
            services.AddSingleton<ITourMediaStorage>(storage);
        });
        var seed = await SeedOwnedTourAsync(factory, TourStatus.Pending);
        using var client = factory.CreateAuthenticatedClient(seed.ActorId, UserRole.TourOperator);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Accessible image"), "AltText");
        form.Add(new ByteArrayContent([1, 2, 3]), "File", "tour.png");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/operator/tours/42/media")
        { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.Should().Contain("tour_media.tour_media_change_locked");
        storage.UploadCount.Should().Be(0);
    }

    private static async Task<(long ActorId, long TourId)> SeedOwnedTourAsync(
        TripMateApiFactory factory,
        TourStatus status = TourStatus.Draft) =>
        await factory.WithDbContextAsync(async db =>
        {
            const long actorId = 700;
            var user = new User
            {
                Id = actorId,
                Email = $"operator-{Guid.NewGuid():N}@example.com",
                FullName = "Test Tour Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
            };
            db.Users.Add(user);
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = actorId,
                User = user,
                CompanyName = "Test Operator Company",
                TaxCode = $"TAX-{Guid.NewGuid():N}",
                BusinessLicenseNo = "TEST-LICENSE",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), actorId);
            Set(tour, nameof(Tour.Title), "Operator owned tour");
            Set(tour, nameof(Tour.BasePrice), 1_000_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), status);
            Set(tour, nameof(Tour.Id), 42L);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            return (actorId, tour.Id);
        });

    private static Task<bool> SeedSecondOperatorAsync(TripMateApiFactory factory, long actorId) =>
        factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Id = actorId,
                Email = $"operator-{Guid.NewGuid():N}@example.com",
                FullName = "Other Tour Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
            };
            db.Users.Add(user);
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = actorId,
                User = user,
                CompanyName = "Other Operator Company",
                TaxCode = $"TAX-{Guid.NewGuid():N}",
                BusinessLicenseNo = "OTHER-LICENSE",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            await db.SaveChangesAsync();
            return true;
        });

    private static void Set<T>(object instance, string name, T value) =>
        instance.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static JsonElement GetPropertyIgnoreCase(JsonElement element, string name) =>
        element.EnumerateObject().First(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class FakeImageInspector : ITourMediaImageInspector
    {
        public Task<TourMediaImageInspectionResult> InspectAsync(
            TourMediaImageSource source, CancellationToken cancellationToken) =>
            Task.FromResult(TourMediaImageInspectionResult.Accepted(new(
                [1, 2, 3], "image/png", ".png", 1, 1,
                "A7D4B398C2B7B56F18AB501ED864E3B40D6C0F5B12A812CE1FC1ACBDBB9B67D0")));
    }

    private sealed class FakeMediaStorage : ITourMediaStorage
    {
        private int _uploadCount;
        public int UploadCount => _uploadCount;
        public bool FailUpload { get; init; }
        public string AllocatePublicId(long tourId) => $"tripmate/tours/{tourId}/opaque-test-asset";

        public Task<TourMediaStorageUploadResult> UploadAsync(
            TourMediaStorageUpload request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _uploadCount);
            if (FailUpload)
            {
                return Task.FromResult(TourMediaStorageUploadResult.Failed(
                    TourMediaStorageFailureKind.Transient, "provider-private-body"));
            }

            return Task.FromResult(TourMediaStorageUploadResult.Succeeded(
                new Uri("https://res.cloudinary.com/test/image/upload/opaque-test-asset.webp")));
        }

        public Task<TourMediaStorageDeleteResult> DestroyAsync(
            string publicId, CancellationToken cancellationToken) =>
            Task.FromResult(new TourMediaStorageDeleteResult(TourMediaStorageDeleteOutcome.Deleted, null));
    }
}