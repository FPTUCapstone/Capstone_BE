using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>
/// TM-106 (UC-60 §3.9.7.1) endpoint integration tests.
/// Verifies Staff authorization, MSG126 mapping, session/active state validation,
/// checklist validation, Pending status guard (MSG133), rich audit snapshot persistence,
/// and immediate public discoverability activation upon approval.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ApproveTourEndpointTests
{
    private static readonly TourReviewChecklistDto ValidChecklist = new(
        ContentCompleteness: true,
        ImageAppropriateness: true,
        PricePlausibility: true,
        PolicyCompliance: true,
        ItineraryFeasibility: true);

    [Fact]
    public async Task Post_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tours/1/approve",
            new { checklist = ValidChecklist });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task Post_WithNonStaffRole_ReturnsForbiddenWithMsg126ProblemDetails(UserRole nonStaffRole)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, nonStaffRole);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tours/1/approve",
            new { checklist = ValidChecklist });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
        body.RootElement.GetProperty("title").GetString().Should().Be("You do not have permission to access this function.");
    }

    [Fact]
    public async Task Post_WithInactiveStaff_ReturnsUnauthorizedViaSessionValidation()
    {
        await using var factory = new TripMateApiFactory(authenticationMode: ApiTestAuthenticationMode.JwtBearer);

        var staff = new User
        {
            Id = 55,
            Email = "inactive-staff@tripmate.test",
            FullName = "Inactive Staff",
            Role = UserRole.Staff,
            Status = AccountStatus.Inactive,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var token = await factory.IssueTokenForPersistedUserAsync(staff);
        using var client = factory.CreateJwtClient(token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tours/1/approve",
            new { checklist = ValidChecklist });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WhenTourDoesNotExist_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tours/999999/approve",
            new { checklist = ValidChecklist });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.TourNotFound);
    }

    [Theory]
    [InlineData(TourStatus.Draft)]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Published)]
    public async Task Post_WhenTourIsNotPending_ReturnsConflictWithMsg133(TourStatus nonPendingStatus)
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, nonPendingStatus);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{tourId}/approve",
            new { checklist = ValidChecklist });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("The selected package is no longer in the Pending Approval status.");
    }

    [Fact]
    public async Task Post_WithMissingChecklist_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Pending);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{tourId}/approve",
            new { checklist = (TourReviewChecklistDto?)null, note = "Some note" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public async Task Post_WithIncompleteChecklistCriterion_ReturnsBadRequest(
        bool completeness, bool images, bool pricing, bool policy, bool itinerary)
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Pending);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var checklist = new TourReviewChecklistDto(
            ContentCompleteness: completeness,
            ImageAppropriateness: images,
            PricePlausibility: pricing,
            PolicyCompliance: policy,
            ItineraryFeasibility: itinerary);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{tourId}/approve",
            new { checklist });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_WithExcessiveNoteLength_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Pending);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var longNote = new string('x', 1001);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{tourId}/approve",
            new { checklist = ValidChecklist, note = longNote });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_WithValidPendingTour_ApprovesAtomically_PersistsAuditSnapshot_AndActivatesPublicDiscoverability()
    {
        await using var factory = new TripMateApiFactory();
        const long staffId = 20;
        var tourId = await SeedTourAsync(factory, TourStatus.Pending);

        // Before approval: public tour detail query returns 404 Not Found
        using (var guestClient = factory.CreateClient())
        {
            var initialDetailResponse = await guestClient.GetAsync($"/api/v1/tours/{tourId}");
            initialDetailResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // Staff approves the tour with valid checklist and internal review note
        const string internalNote = "All 5 review criteria verified. Approved for launch.";
        using var staffClient = factory.CreateAuthenticatedClient(staffId, UserRole.Staff);

        var approveResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{tourId}/approve",
            new { checklist = ValidChecklist, note = internalNote });

        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveBody = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
        approveBody.RootElement.GetProperty("tourId").GetInt64().Should().Be(tourId);
        approveBody.RootElement.GetProperty("status").GetString().Should().Be(TourStatus.Approved.ToString());

        // Verify database state: Tour entity and AuditLog
        await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tourId);
            tour.Status.Should().Be(TourStatus.Approved);
            tour.ReviewedBy.Should().Be(staffId);
            tour.ReviewedAtUtc.Should().NotBeNull();
            tour.PublishedAtUtc.Should().NotBeNull();
            tour.PublishedAtUtc.Should().Be(tour.ReviewedAtUtc);

            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a =>
                a.AffectedEntityId == tourId && a.ActionType == AuditActionTypes.TourApproved);
            audit.ActorUserId.Should().Be(staffId);
            audit.AffectedEntity.Should().Be(AuditEntityTypes.Tour);
            audit.Result.Should().Be(AuditOutcome.Success);

            using var afterJson = JsonDocument.Parse(audit.AfterData!);
            var root = afterJson.RootElement;
            root.GetProperty("Status").GetString().Should().Be(TourStatus.Approved.ToString());
            root.GetProperty("VersionNo").GetInt32().Should().Be(tour.VersionNo);
            root.GetProperty("VersionRootTourId").GetInt64().Should().Be(tour.VersionRootTourId!.Value);
            root.GetProperty("ReviewedBy").GetInt64().Should().Be(staffId);
            root.GetProperty("PublishedAtUtc").GetDateTimeOffset().Should().Be(tour.PublishedAtUtc!.Value);
            root.GetProperty("InternalNote").GetString().Should().Be(internalNote);

            var checklistJson = root.GetProperty("Checklist");
            checklistJson.GetProperty("ContentCompleteness").GetBoolean().Should().BeTrue();
            checklistJson.GetProperty("ImageAppropriateness").GetBoolean().Should().BeTrue();
            checklistJson.GetProperty("PricePlausibility").GetBoolean().Should().BeTrue();
            checklistJson.GetProperty("PolicyCompliance").GetBoolean().Should().BeTrue();
            checklistJson.GetProperty("ItineraryFeasibility").GetBoolean().Should().BeTrue();

            return true;
        });

        // After approval: public discoverability is immediately active
        using (var guestClient = factory.CreateClient())
        {
            var postApprovalDetailResponse = await guestClient.GetAsync($"/api/v1/tours/{tourId}");
            postApprovalDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var detailBody = JsonDocument.Parse(await postApprovalDetailResponse.Content.ReadAsStringAsync());
            detailBody.RootElement.GetProperty("tourId").GetString().Should().Be(tourId.ToString());
            detailBody.RootElement.GetProperty("title").GetString().Should().Be("Test Tour Package");
        }
    }

    [Fact]
    public async Task Post_SequentialSecondApproval_ReturnsConflictWithMsg133()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Pending);
        using var client = factory.CreateAuthenticatedClient(11, UserRole.Staff);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/tours/{tourId}/approve", new { checklist = ValidChecklist });
        var second = await client.PostAsJsonAsync($"/api/v1/admin/tours/{tourId}/approve", new { checklist = ValidChecklist });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("The selected package is no longer in the Pending Approval status.");
    }

    private static Task<long> SeedTourAsync(TripMateApiFactory factory, TourStatus desiredStatus) =>
        factory.WithDbContextAsync(async db =>
        {
            const long operatorId = 101;
            var now = DateTimeOffset.UtcNow;

            var existingUser = await db.Users.FindAsync(operatorId);
            if (existingUser is null)
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
                    CompanyName = "TM-106 Tours",
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

                var poi = PointOfInterest.Create(category, "Ancient Town", 15.88m, 108.33m, operatorId, now);
                db.PointsOfInterest.Add(poi);

                var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
                typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Hoi An");
                db.Destinations.Add(destination);

                var policy = CancellationPolicyTemplate.Create("Standard Policy", null, now,
                    [CancellationPolicyTemplateRule.Create(30, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
                db.CancellationPolicyTemplates.Add(policy);

                await db.SaveChangesAsync();
            }

            var categoryEntry = await db.PoiCategories.FirstAsync();
            var destinationEntry = await db.Destinations.FirstAsync();
            var policyEntry = await db.CancellationPolicyTemplates.FirstAsync();
            var poiEntry = await db.PointsOfInterest.FirstAsync();

            var tour = Tour.CreateDraft(
                operatorId,
                categoryId: null,
                "Test Tour Package",
                "A verified tour for testing approval workflow.",
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

            tour.SetVersionLineage(9000 + tour.Id, 1);

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
                case TourStatus.Published:
                    tour.SubmitForReview(now);
                    tour.Approve(10, now);
                    tour.Publish(10, now);
                    break;
            }

            db.Tours.Add(tour);
            await db.SaveChangesAsync();

            // Fix VersionRootTourId if needed
            if (tour.VersionRootTourId is null or <= 0)
            {
                tour.SetVersionLineage(tour.Id, 1);
                await db.SaveChangesAsync();
            }

            return tour.Id;
        });
}
