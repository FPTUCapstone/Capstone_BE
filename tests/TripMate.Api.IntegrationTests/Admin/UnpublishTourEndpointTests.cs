using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>
/// TM-106 Gate 1 API endpoint tests for Unpublish compatibility (Approved with active publication & Published).
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class UnpublishTourEndpointTests
{
    [Fact]
    public async Task Post_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/admin/tours/1/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task Post_WithNonStaffRole_ReturnsForbiddenWithMsg126(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(50, role);

        var response = await client.PostAsync("/api/v1/admin/tours/1/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("You do not have permission to access this function.");
    }

    [Fact]
    public async Task Post_WhenTourNotFound_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var response = await client.PostAsync("/api/v1/admin/tours/999999/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(TourStatus.Draft, false, false)]
    [InlineData(TourStatus.Pending, false, false)]
    [InlineData(TourStatus.Rejected, false, false)]
    [InlineData(TourStatus.Inactive, false, false)]
    [InlineData(TourStatus.Approved, false, false)] // Approved with PublishedAtUtc == null
    [InlineData(TourStatus.Approved, true, true)]   // Approved with future PublishedAtUtc
    public async Task Post_WhenTourIneligible_ReturnsConflict(TourStatus status, bool withPublishedAt, bool futurePublishedAt)
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, status, withPublishedAt, futurePublishedAt);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        var response = await client.PostAsync($"/api/v1/admin/tours/{tourId}/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Post_WhenApprovedWithActivePublishedAt_TransitionsToInactiveRecordsApprovedBeforeStatusAndHidesDetail()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Approved, withPublishedAt: true, futurePublishedAt: false);

        using var guestClient = factory.CreateClient();
        (await guestClient.GetAsync($"/api/v1/tours/{tourId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        const long staffId = 25;
        using var staffClient = factory.CreateAuthenticatedClient(staffId, UserRole.Staff);
        var response = await staffClient.PostAsync($"/api/v1/admin/tours/{tourId}/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("tourId").GetInt64().Should().Be(tourId);
        payload.RootElement.GetProperty("status").GetString().Should().Be(TourStatus.Inactive.ToString());

        // Public detail is now hidden (404)
        (await guestClient.GetAsync($"/api/v1/tours/{tourId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Duplicate unpublish fails cleanly with 409 Conflict
        var duplicate = await staffClient.PostAsync($"/api/v1/admin/tours/{tourId}/unpublish", null);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tourId);
            tour.Status.Should().Be(TourStatus.Inactive);
            tour.PublishedAtUtc.Should().NotBeNull();
            tour.ReviewedBy.Should().Be(10);

            var audits = await db.AuditLogs.AsNoTracking()
                .Where(a => a.AffectedEntityId == tourId && a.ActionType == AuditActionTypes.TourUnpublished)
                .ToListAsync();
            audits.Should().ContainSingle();
            audits[0].ActorUserId.Should().Be(staffId);

            using var beforeJson = JsonDocument.Parse(audits[0].BeforeData!);
            beforeJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Approved.ToString());

            using var afterJson = JsonDocument.Parse(audits[0].AfterData!);
            afterJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Inactive.ToString());
            afterJson.RootElement.GetProperty("ModeratedBy").GetInt64().Should().Be(staffId);
            return true;
        });
    }

    [Fact]
    public async Task Post_WhenPublishedTour_PreservesExistingBehaviorAndRecordsPublishedBeforeStatus()
    {
        await using var factory = new TripMateApiFactory();
        var tourId = await SeedTourAsync(factory, TourStatus.Published, withPublishedAt: true, futurePublishedAt: false);
        using var staffClient = factory.CreateAuthenticatedClient(26, UserRole.Staff);

        var response = await staffClient.PostAsync($"/api/v1/admin/tours/{tourId}/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tourId);
            tour.Status.Should().Be(TourStatus.Inactive);

            var audit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(a => a.AffectedEntityId == tourId && a.ActionType == AuditActionTypes.TourUnpublished);
            using var beforeJson = JsonDocument.Parse(audit.BeforeData!);
            beforeJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Published.ToString());
            return true;
        });
    }

    [Fact]
    public async Task Post_WhenPersistenceFails_ReturnsMsg127AndPreservesApprovedState()
    {
        var failingInterceptor = new FailOnTourUnpublishSaveInterceptor();
        await using var factory = new TripMateApiFactory(saveChangesInterceptor: failingInterceptor);
        var tourId = await SeedTourAsync(factory, TourStatus.Approved, withPublishedAt: true, futurePublishedAt: false);
        using var staffClient = factory.CreateAuthenticatedClient(27, UserRole.Staff);

        var response = await staffClient.PostAsync($"/api/v1/admin/tours/{tourId}/unpublish", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("TripMate is temporarily unable to process your request. Please check your connection and try again.");

        await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tourId);
            tour.Status.Should().Be(TourStatus.Approved);
            var audits = await db.AuditLogs.AsNoTracking()
                .Where(a => a.AffectedEntityId == tourId && a.ActionType == AuditActionTypes.TourUnpublished)
                .ToListAsync();
            audits.Should().BeEmpty();
            return true;
        });
    }

    private sealed class FailOnTourUnpublishSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var unpublishing = eventData.Context?.ChangeTracker
                .Entries<Tour>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == TourStatus.Inactive) == true;

            if (unpublishing)
            {
                throw new DbUpdateException("Simulated unpublish persistence failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private static Task<long> SeedTourAsync(
        TripMateApiFactory factory,
        TourStatus desiredStatus,
        bool withPublishedAt,
        bool futurePublishedAt) =>
        factory.WithDbContextAsync(async db =>
        {
            const long operatorId = 201;
            var now = DateTimeOffset.UtcNow;

            if (await db.Users.FindAsync(operatorId) is null)
            {
                var user = new User
                {
                    Id = operatorId,
                    Email = $"operator-{operatorId}@test.local",
                    FullName = "Operator Unpublish",
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
                    CompanyName = "TM-106 Unpublish Tours",
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
            var policyEntry = await db.CancellationPolicyTemplates.FirstAsync();
            var poiEntry = await db.PointsOfInterest.FirstAsync();

            var tour = Tour.CreateDraft(
                operatorId,
                categoryId: null,
                "Gate 1 Unpublish Tour",
                "Verified tour for Gate 1 unpublish testing.",
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
                    tour.SubmitForReview(now.AddMinutes(-30));
                    break;
                case TourStatus.Rejected:
                    tour.SubmitForReview(now.AddMinutes(-30));
                    tour.Reject(10, "Initial rejection", now.AddMinutes(-15));
                    break;
                case TourStatus.Approved:
                    tour.SubmitForReview(now.AddMinutes(-30));
                    DateTimeOffset? pubAt = withPublishedAt
                        ? (futurePublishedAt ? now.AddHours(2) : now.AddMinutes(-10))
                        : null;
                    tour.Approve(10, now.AddMinutes(-15), publishedAtUtc: pubAt);
                    break;
                case TourStatus.Published:
                    tour.SubmitForReview(now.AddMinutes(-30));
                    tour.Approve(10, now.AddMinutes(-20));
                    tour.Publish(10, now.AddMinutes(-10));
                    break;
                case TourStatus.Inactive:
                    tour.SubmitForReview(now.AddMinutes(-30));
                    tour.Approve(10, now.AddMinutes(-20), publishedAtUtc: now.AddMinutes(-20));
                    tour.Unpublish(now.AddMinutes(-10));
                    break;
            }

            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            tour.SetVersionLineage(tour.Id, 1);
            await db.SaveChangesAsync();
            return tour.Id;
        });
}
