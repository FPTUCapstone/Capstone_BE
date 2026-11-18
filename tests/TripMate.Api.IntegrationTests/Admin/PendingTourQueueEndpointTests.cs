using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>UC-60 Screens 13/14 read endpoints: Staff-only pending queue and review detail.</summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PendingTourQueueEndpointTests
{
    private const string QueuePath = "/api/v1/admin/tours/pending";

    [Fact]
    public async Task Get_WithoutAuthentication_ReturnsUnauthorizedOnBothRoutes()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        (await client.GetAsync(QueuePath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/admin/tours/1/review")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task Get_WithNonStaffRole_ReturnsForbiddenWithMsg126OnBothRoutes(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, role);

        foreach (var path in new[] { QueuePath, "/api/v1/admin/tours/1/review" })
        {
            var response = await client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: path);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
        }
    }

    [Fact]
    public async Task GetQueue_AsStaff_ReturnsOnlyPendingToursWithPagingEnvelope()
    {
        await using var factory = new TripMateApiFactory();
        var pendingId = await SeedTourAsync(factory, TourStatus.Pending, "Pending lantern tour");
        await SeedTourAsync(factory, TourStatus.Approved, "Already approved tour");
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync($"{QueuePath}?pageNumber=1&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("totalCount").GetInt32().Should().Be(1);
        root.GetProperty("totalPages").GetInt32().Should().Be(1);
        root.GetProperty("pageNumber").GetInt32().Should().Be(1);
        root.GetProperty("pageSize").GetInt32().Should().Be(20);
        var item = root.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("tourId").GetInt64().Should().Be(pendingId);
        item.GetProperty("tourName").GetString().Should().Be("Pending lantern tour");
        item.GetProperty("operatorName").GetString().Should().Be("Queue Tours");
        item.GetRawText().Should().NotContain("0901234567", "operator contact data must not leak into the queue");
    }

    [Fact]
    public async Task GetQueue_WithKeyword_FiltersAndReportsTotals()
    {
        await using var factory = new TripMateApiFactory();
        await SeedTourAsync(factory, TourStatus.Pending, "Lantern night");
        await SeedTourAsync(factory, TourStatus.Pending, "Mountain trek");
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync($"{QueuePath}?keyword=lantern");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("items")[0].GetProperty("tourName").GetString().Should().Be("Lantern night");
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("pageNumber=0")]
    [InlineData("pageNumber=-3")]
    public async Task GetQueue_WithInvalidPaging_ReturnsBadRequest(string query)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync($"{QueuePath}?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetQueue_WithOversizedKeyword_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync($"{QueuePath}?keyword={new string('a', 101)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetReview_ForPendingTour_ReturnsChecklistInputs()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Pending, "Review me");
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync($"/api/v1/admin/tours/{tourId}/review");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("tourId").GetInt64().Should().Be(tourId);
        root.GetProperty("overview").GetString().Should().Be("A verified tour for the review queue.");
        root.GetProperty("cancellationPolicy").GetArrayLength().Should().Be(2);
        root.GetProperty("schedules").GetArrayLength().Should().Be(1);
        root.GetProperty("itinerary").GetArrayLength().Should().Be(1);
        root.GetProperty("itinerary")[0].GetProperty("poiName").GetString().Should().Be("Ancient Town");
        root.GetRawText().Should().NotContain("0901234567").And.NotContain("TAX-");
    }

    [Fact]
    public async Task GetReview_WhenTourDoesNotExist_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync("/api/v1/admin/tours/404404/review");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Draft)]
    public async Task GetReview_WhenTourIsNotPending_ReturnsConflict(TourStatus status)
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, status, "Not pending");
        using var client = factory.CreateAuthenticatedClient(55, UserRole.Staff);

        var response = await client.GetAsync($"/api/v1/admin/tours/{tourId}/review");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
    }

    private static Task<long> SeedTourAsync(TripMateApiFactory factory, TourStatus desiredStatus, string title) =>
        factory.WithDbContextAsync(async db =>
        {
            const long operatorId = 101;
            var now = DateTimeOffset.UtcNow;

            if (await db.Users.FindAsync(operatorId) is null)
            {
                var user = new User
                {
                    Id = operatorId,
                    Email = $"operator-{operatorId}@test.local",
                    FullName = "Operator for Tour",
                    Role = UserRole.TourOperator,
                    Status = AccountStatus.Active,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                db.Users.Add(user);
                db.OperatorProfiles.Add(new OperatorProfile
                {
                    UserId = operatorId,
                    User = user,
                    CompanyName = "Queue Tours",
                    TaxCode = $"TAX-{operatorId}",
                    BusinessLicenseNo = $"LIC-{operatorId}",
                    ContactPhone = "0901234567",
                    ContactAddress = "Hoi An",
                    ApprovalStatus = OperatorApprovalStatus.Approved,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });

                var category = PoiCategory.Create("Heritage Site", null);
                db.PoiCategories.Add(category);
                db.PointsOfInterest.Add(PointOfInterest.Create(category, "Ancient Town", 15.88m, 108.33m, operatorId, now));

                var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
                typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Hoi An");
                db.Destinations.Add(destination);

                db.CancellationPolicyTemplates.Add(CancellationPolicyTemplate.Create("Standard Policy", null, now,
                    [CancellationPolicyTemplateRule.Create(30, 0), CancellationPolicyTemplateRule.Create(0, 100)]));
                await db.SaveChangesAsync();
            }

            var destinationEntry = await db.Destinations.FirstAsync();
            var policyEntry = await db.CancellationPolicyTemplates.Include(p => p.Rules).FirstAsync();
            var poiEntry = await db.PointsOfInterest.FirstAsync();

            var tour = Tour.CreateDraft(
                operatorId,
                categoryId: null,
                title,
                "A verified tour for the review queue.",
                1_500_000m,
                durationDays: 1,
                destinationIds: [destinationEntry.Id],
                destinationSummary: "Hoi An",
                policyEntry,
                [TourItineraryItem.Create(1, 1, poiEntry.Id, null, 60, null)],
                [TourSchedule.Create(now.AddDays(10), now.AddDays(11), "Meeting Point", 15, now, adultPrice: 1_500_000m)],
                now,
                "Meals included",
                "Personal expenses");

            switch (desiredStatus)
            {
                case TourStatus.Draft:
                    break;
                case TourStatus.Pending:
                    tour.SubmitForReview(now);
                    break;
                case TourStatus.Approved:
                    tour.SubmitForReview(now);
                    tour.Approve(10, now);
                    break;
                case TourStatus.Rejected:
                    tour.SubmitForReview(now);
                    tour.Reject(10, "Initial rejection", now);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(desiredStatus));
            }

            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            tour.SetVersionLineage(tour.Id, 1);
            await db.SaveChangesAsync();
            return tour.Id;
        });
}