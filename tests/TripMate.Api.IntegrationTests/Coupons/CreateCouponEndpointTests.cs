using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Coupons;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateCouponEndpointTests
{
    private const string Endpoint = "/api/v1/operator/coupons";

    [Fact]
    public async Task Create_WithoutAuthentication_Returns401()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Endpoint, ValidRequest(1));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WithTravelerRole_Returns403()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Endpoint, ValidRequest(1));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEligibleTours_ReturnsOnlyTheCallersApprovedTours()
    {
        using var factory = new TripMateApiFactory();
        var eligibleId = await SeedApprovedOperatorAndTourAsync(factory, 7);
        await SeedTourAsync(factory, 7, TourStatus.Draft);
        await SeedApprovedOperatorAndTourAsync(factory, 8);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var response = await client.GetAsync($"{Endpoint}/eligible-tours");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetArrayLength().Should().Be(1);
        payload.RootElement[0].GetProperty("tourId").GetInt64().Should().Be(eligibleId);
    }

    [Fact]
    public async Task Create_WithApprovedOwnedTours_PersistsCouponAndLinks()
    {
        using var factory = new TripMateApiFactory();
        var tourId = await SeedApprovedOperatorAndTourAsync(factory, 7);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var response = await client.PostAsJsonAsync(Endpoint, ValidRequest(tourId));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("code").GetString().Should().Be("SUMMER10");
        await factory.WithDbContextAsync(async db =>
        {
            var voucher = await db.Vouchers.Include(item => item.ApplicableTours).SingleAsync();
            voucher.OwnerOperatorUserId.Should().Be(7);
            voucher.ApplicableTours.Select(link => link.TourId).Should().Equal(tourId);
            return true;
        });
    }

    [Fact]
    public async Task Create_WithOtherOperatorsTour_Returns403WithoutPersistingCoupon()
    {
        using var factory = new TripMateApiFactory();
        await SeedApprovedOperatorAndTourAsync(factory, 7);
        var foreignTourId = await SeedApprovedOperatorAndTourAsync(factory, 8);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var response = await client.PostAsJsonAsync(Endpoint, ValidRequest(foreignTourId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be("coupon.tour_not_owned");
        (await factory.WithDbContextAsync(db => db.Vouchers.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Create_WithMissingTourScope_Returns400ProblemDetails()
    {
        using var factory = new TripMateApiFactory();
        var tourId = await SeedApprovedOperatorAndTourAsync(factory, 7);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);
        var request = ValidRequest(tourId) with { ApplicableTourIds = [] };

        using var response = await client.PostAsJsonAsync(Endpoint, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task OperatorCouponLifecycle_ListsReadsAndChangesOnlyOwnedCoupon()
    {
        using var factory = new TripMateApiFactory();
        var tourId = await SeedApprovedOperatorAndTourAsync(factory, 7);
        var replacementTourId = await SeedTourAsync(factory, 7, TourStatus.Published);
        var couponId = await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.SingleAsync(item => item.Id == tourId);
            var voucher = Voucher.Create(7, "LIFECYCLE10", "Lifecycle coupon",
                VoucherDiscountType.Percentage, 10m, 100_000m, 0m, 10, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), [tour], DateTimeOffset.UtcNow);
            db.Vouchers.Add(voucher);
            await db.SaveChangesAsync();
            return voucher.Id;
        });
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var listResponse = await client.GetAsync(Endpoint);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        list.RootElement[0].GetProperty("name").GetString().Should().Be("Lifecycle coupon");

        using var readResponse = await client.GetAsync($"{Endpoint}/{couponId}");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        read.RootElement.GetProperty("ownerOperatorUserId").GetInt64().Should().Be(7);

        using var updateResponse = await client.PutAsJsonAsync($"{Endpoint}/{couponId}", new
        {
            Name = "Updated lifecycle coupon",
            DiscountType = VoucherDiscountType.Percentage,
            DiscountValue = 15m,
            MaxDiscountAmount = 150_000m,
            MinOrderAmount = 0m,
            UsageLimit = 10,
            UsageLimitPerUser = 1,
            ValidFromUtc = DateTimeOffset.UtcNow,
            ValidToUtc = DateTimeOffset.UtcNow.AddDays(14),
            ApplicableTourIds = new[] { replacementTourId },
        });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, await updateResponse.Content.ReadAsStringAsync());
        using var update = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
        update.RootElement.GetProperty("name").GetString().Should().Be("Updated lifecycle coupon");
        update.RootElement.GetProperty("discountValue").GetDecimal().Should().Be(15m);
        await factory.WithDbContextAsync(async db =>
        {
            var voucher = await db.Vouchers.Include(item => item.ApplicableTours).SingleAsync(item => item.Id == couponId);
            voucher.ApplicableTours.Select(item => item.TourId).Should().Equal(replacementTourId);
            return true;
        });

        using var deactivateResponse = await client.PostAsync($"{Endpoint}/{couponId}/deactivate", null);
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var deactivate = JsonDocument.Parse(await deactivateResponse.Content.ReadAsStringAsync());
        deactivate.RootElement.GetProperty("status").GetString().Should().Be("Disabled");

        using var activateResponse = await client.PostAsync($"{Endpoint}/{couponId}/activate", null);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var activate = JsonDocument.Parse(await activateResponse.Content.ReadAsStringAsync());
        activate.RootElement.GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task Swagger_DescribesCreateCouponContract()
    {
        using var factory = new TripMateApiFactory(environmentName: "Development");
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var post = document.RootElement.GetProperty("paths").GetProperty(Endpoint).GetProperty("post");
        post.GetProperty("responses").TryGetProperty("201", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("400", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("404", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("409", out _).Should().BeTrue();
        post.GetProperty("responses").TryGetProperty("422", out _).Should().BeTrue();
    }

    private static CouponRequest ValidRequest(long tourId) => new(
        "summer10",
        "Summer ten percent",
        VoucherDiscountType.Percentage,
        10m,
        200_000m,
        0m,
        100,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7),
        [tourId]);

    private static async Task<long> SeedApprovedOperatorAndTourAsync(TripMateApiFactory factory, long operatorId) =>
        await SeedTourAsync(factory, operatorId, TourStatus.Published, seedOperator: true);

    private static async Task<long> SeedTourAsync(
        TripMateApiFactory factory,
        long operatorId,
        TourStatus status,
        bool seedOperator = false) =>
        await factory.WithDbContextAsync(async db =>
        {
            if (seedOperator)
            {
                var user = new User
                {
                    Id = operatorId,
                    Email = $"operator-{operatorId}@test.local",
                    FullName = "Approved operator",
                    Role = UserRole.TourOperator,
                    Status = AccountStatus.Active,
                };
                var profile = new OperatorProfile
                {
                    UserId = operatorId,
                    User = user,
                    CompanyName = "TripMate Tours",
                    TaxCode = $"TAX{operatorId}",
                    BusinessLicenseNo = $"LIC{operatorId}",
                    ApprovalStatus = OperatorApprovalStatus.Approved,
                };
                db.Users.Add(user);
                db.OperatorProfiles.Add(profile);
            }

            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), operatorId);
            Set(tour, nameof(Tour.Title), "Approved tour");
            Set(tour, nameof(Tour.BasePrice), 1_000_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), status);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            return tour.Id;
        });

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record CouponRequest(
        string Code,
        string Name,
        VoucherDiscountType DiscountType,
        decimal DiscountValue,
        decimal? MaxDiscountAmount,
        decimal MinOrderAmount,
        int? UsageLimit,
        int? UsageLimitPerUser,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset ValidToUtc,
        IReadOnlyCollection<long> ApplicableTourIds);
}
