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
public sealed class UpdateCouponEndpointTests
{
    private const string Endpoint = "/api/v1/operator/coupons";

    [Fact]
    public async Task Put_RequiresAuthenticatedOwner()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        var request = Request(seed);
        using var anonymous = factory.CreateClient();
        using var traveler = factory.CreateAuthenticatedClient(7, UserRole.Traveler);
        using var foreignOperator = factory.CreateAuthenticatedClient(8, UserRole.TourOperator);

        (await anonymous.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}", request)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await traveler.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}", request)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await foreignOperator.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}", request)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_UpdatesOwnedCouponAndReadReturnsSavedValues()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);
        var request = Request(seed) with { Name = "Autumn discount", DiscountValue = 20m, UsageLimit = null };

        using var update = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}", request);
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        using var read = await client.GetAsync($"{Endpoint}/{seed.CouponId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("name").GetString().Should().Be("Autumn discount");
        payload.RootElement.GetProperty("discountValue").GetDecimal().Should().Be(20m);
        payload.RootElement.GetProperty("usageLimit").ValueKind.Should().Be(JsonValueKind.Null);
        payload.RootElement.GetProperty("code").GetString().Should().Be("SAVE10");
        payload.RootElement.GetProperty("applicableTourIds")[0].GetInt64().Should().Be(seed.TourId);
    }

    [Fact]
    public async Task Put_RejectsDiscountTypeChangeAndMovedElapsedStart()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var type = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}",
            Request(seed) with { DiscountType = VoucherDiscountType.Flat, MaxDiscountAmount = null });
        type.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var start = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}",
            Request(seed) with { ValidFromUtc = seed.ValidFromUtc.AddHours(-1) });
        start.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await factory.WithDbContextAsync(db => db.Vouchers.SingleAsync(v => v.Id == seed.CouponId)))
            .DiscountValue.Should().Be(10m);
    }

    [Fact]
    public async Task Put_LimitBelowOccupiedUsageReturns409WithoutChangingCoupon()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, usedCount: 2);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var conflict = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}",
            Request(seed) with { UsageLimit = 1, Name = "Must not save" });

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be("coupon.usage_conflict");
        var persisted = await factory.WithDbContextAsync(db => db.Vouchers.SingleAsync(v => v.Id == seed.CouponId));
        persisted.Name.Should().Be("Original");
        persisted.UsedCount.Should().Be(2);
    }

    [Fact]
    public async Task Put_ForeignTourReturns403WithoutPartialUpdate()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        var foreignTourId = await factory.WithDbContextAsync(async db =>
        {
            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), 8L);
            Set(tour, nameof(Tour.Title), "Foreign tour");
            Set(tour, nameof(Tour.BasePrice), 1_000_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            return tour.Id;
        });
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var response = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}",
            Request(seed) with { Name = "Must not save", ApplicableTourIds = [seed.TourId, foreignTourId] });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var persisted = await factory.WithDbContextAsync(db => db.Vouchers
            .Include(voucher => voucher.ApplicableTours).SingleAsync(voucher => voucher.Id == seed.CouponId));
        persisted.Name.Should().Be("Original");
        persisted.ApplicableTours.Select(link => link.TourId).Should().Equal(seed.TourId);
    }

    [Fact]
    public async Task Put_UsageLimitEqualToOccupiedUsageIsAccepted()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, usedCount: 2);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var response = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}",
            Request(seed) with { UsageLimit = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("usageLimit").GetInt32().Should().Be(2);
        payload.RootElement.GetProperty("usedCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Put_ExpiredExtensionNeedsSeparateActivation()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, expired: true);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var update = await client.PutAsJsonAsync($"{Endpoint}/{seed.CouponId}", Request(seed));
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        using var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        updated.RootElement.GetProperty("status").GetString().Should().Be(nameof(VoucherStatus.Disabled));

        using var activate = await client.PostAsync($"{Endpoint}/{seed.CouponId}/activate", null);
        activate.StatusCode.Should().Be(HttpStatusCode.OK);
        using var activated = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
        activated.RootElement.GetProperty("status").GetString().Should().Be(nameof(VoucherStatus.Active));
    }

    [Fact]
    public async Task Activate_WhenApplicableTourIsNoLongerPublic_Returns422AndStaysDisabled()
    {
        using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        using var deactivate = await client.PostAsync($"{Endpoint}/{seed.CouponId}/deactivate", null);
        deactivate.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.SingleAsync(candidate => candidate.Id == seed.TourId);
            Set(tour, nameof(Tour.Status), TourStatus.Inactive);
            await db.SaveChangesAsync();
            return true;
        });

        using var activate = await client.PostAsync($"{Endpoint}/{seed.CouponId}/activate", null);

        activate.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be("coupon.tour_not_eligible");
        (await factory.WithDbContextAsync(db => db.Vouchers.AsNoTracking()
            .SingleAsync(voucher => voucher.Id == seed.CouponId))).Status.Should().Be(VoucherStatus.Disabled);
    }

    private static UpdateRequest Request(Seed seed) => new("Updated", VoucherDiscountType.Percentage, 10m,
        200_000m, 0m, 5, 1, seed.ValidFromUtc, DateTimeOffset.UtcNow.AddDays(3), [seed.TourId]);

    private static async Task<Seed> SeedAsync(TripMateApiFactory factory, int usedCount = 0, bool expired = false) =>
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Id = 7,
                Email = "coupon-owner@test.local",
                FullName = "Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active
            };
            var foreign = new User
            {
                Id = 8,
                Email = "coupon-foreign@test.local",
                FullName = "Other operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active
            };
            db.Users.AddRange(user, foreign);
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = user.Id,
                User = user,
                CompanyName = "TripMate Tours",
                TaxCode = "TAX7",
                BusinessLicenseNo = "LIC7",
                ApprovalStatus = OperatorApprovalStatus.Approved
            });
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = foreign.Id,
                User = foreign,
                CompanyName = "Other Tours",
                TaxCode = "TAX8",
                BusinessLicenseNo = "LIC8",
                ApprovalStatus = OperatorApprovalStatus.Approved
            });
            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), user.Id);
            Set(tour, nameof(Tour.Title), "Eligible tour");
            Set(tour, nameof(Tour.BasePrice), 1_000_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            var start = now.AddDays(-2);
            var voucher = Voucher.Create(user.Id, "SAVE10", "Original", VoucherDiscountType.Percentage,
                10m, 200_000m, 0m, 5, 1, start,
                expired ? now.AddDays(-1) : now.AddDays(2), [tour], now.AddDays(-3));
            for (var index = 0; index < usedCount; index++) voucher.IncrementUsage();
            db.Vouchers.Add(voucher);
            await db.SaveChangesAsync();
            return new Seed(voucher.Id, tour.Id, start);
        });

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(long CouponId, long TourId, DateTimeOffset ValidFromUtc);
    private sealed record UpdateRequest(string Name, VoucherDiscountType DiscountType, decimal DiscountValue,
        decimal? MaxDiscountAmount, decimal MinOrderAmount, int? UsageLimit, int? UsageLimitPerUser,
        DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc, IReadOnlyCollection<long> ApplicableTourIds);
}