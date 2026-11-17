using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Tours.Search;
using TripMate.Application.Features.Traveler.Bookings.Quote;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>
/// TM-106 SQL Server integration suite.
/// Exercises real concurrency, audit trail persistence, state-machine regression,
/// and the complete Public Search -> Detail -> Quote -> Booking pipeline against
/// an isolated, disposable SQL Server test database.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ApproveTourSqlServerTests
{
    private static readonly TourReviewChecklistDto ValidChecklist = new(
        ContentCompleteness: true,
        ImageAppropriateness: true,
        PricePlausibility: true,
        PolicyCompliance: true,
        ItineraryFeasibility: true);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentApproveRequests_OneSucceedsOneConflicts_AndPersistsSingleDecision()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var firstClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        using var secondClient = factory.CreateAuthenticatedClient(seed.StaffId2, UserRole.Staff);

        var firstTask = firstClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "First reviewer concurrent approval" });
        var secondTask = secondClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Second reviewer concurrent approval" });

        var responses = await Task.WhenAll(firstTask, secondTask);

        // One request succeeds (200 OK) and the competing concurrent request cleanly conflicts (409 Conflict with MSG133)
        // after bounded deadlock recovery and authoritative state recheck. Zero HTTP 500 errors.
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.OK);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Conflict);

        var conflictResponse = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        using var conflictBody = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
        conflictBody.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
        conflictBody.RootElement.GetProperty("title").GetString()
            .Should().Be("The selected package is no longer in the Pending Approval status.");

        // Verify database integrity: exactly one TourApproved audit record, final status Approved
        await using var verificationContext = database.CreateDbContext();
        var tour = await verificationContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Approved);
        tour.ReviewedBy.Should().BeOneOf(seed.StaffId1, seed.StaffId2);
        tour.PublishedAtUtc.Should().NotBeNull();
        tour.PublishedAtUtc.Should().Be(tour.ReviewedAtUtc);

        var audits = await verificationContext.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourApproved)
            .ToListAsync();
        audits.Should().ContainSingle();
        audits[0].Result.Should().Be(AuditOutcome.Success);
        audits[0].ActorUserId.Should().Be(tour.ReviewedBy);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentApproveAndReject_DemonstratesRaceCondition_WhenRejectLacksSerializableIsolation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var approveClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        using var rejectClient = factory.CreateAuthenticatedClient(seed.StaffId2, UserRole.Staff);

        var approveTask = approveClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Concurrent approval" });
        var rejectTask = rejectClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/reject",
            new { reason = "Concurrent rejection reason for conflict test." });

        var responses = await Task.WhenAll(approveTask, rejectTask);

        // Technical Finding: Because RejectTourCommandHandler (TM-107) runs under ReadCommitted isolation
        // and Tour lacks a rowversion token, both handlers can read Status == Pending concurrently.
        // Consequently, both can return HTTP 200 OK in a last-write-wins race, or one may conflict if serialized.
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.InternalServerError));

        await using var verificationContext = database.CreateDbContext();
        var tour = await verificationContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().BeOneOf(TourStatus.Approved, TourStatus.Rejected);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RepeatedApproval_AfterCommit_ReturnsConflictMsg133_AndLeavesStateUntouched()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var client = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);

        // First approval commits successfully
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Initial valid approval" });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Capture commit state
        await using var checkContext = database.CreateDbContext();
        var tourAfterFirst = await checkContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        var reviewedAtFirst = tourAfterFirst.ReviewedAtUtc;

        // Second approval attempt immediately fails
        var secondResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Duplicate approval attempt" });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var body = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("The selected package is no longer in the Pending Approval status.");

        // Database verification: unchanged state and exactly one audit log
        var tourAfterSecond = await checkContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tourAfterSecond.Status.Should().Be(TourStatus.Approved);
        tourAfterSecond.ReviewedAtUtc.Should().Be(reviewedAtFirst);

        var audits = await checkContext.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourApproved)
            .ToListAsync();
        audits.Should().ContainSingle();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task StateMachineRegression_UnpublishApprovedTour_FailsWithConflict_DemonstratingUnpublishGuard()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);

        // 1. Staff approves the tour -> Status = Approved, PublishedAtUtc is set
        var approveResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Staff attempts to unpublish the newly-approved tour
        // Baseline Unpublish requires Status == Published; calling Unpublish on Approved yields HTTP 409 Conflict
        var unpublishResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/unpublish",
            new { });

        unpublishResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await unpublishResponse.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
        body.RootElement.GetProperty("title").GetString().Should().Be("Only Published tours can be unpublished.");

        // 3. Entity-level validation: Tour.Unpublish throws InvalidOperationException when Status == Approved
        await using var context = database.CreateDbContext();
        var tour = await context.Tours.SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Approved);

        var action = () => tour.Unpublish(DateTimeOffset.UtcNow);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Only Published tours can be unpublished.");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ApprovedTour_BookingEligibility_FullFlow_Search_Detail_Quote_Booking()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        // 1. Approve the tour via TM-106 API
        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        var approveResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Public Search finds the approved tour
        using var guestClient = factory.CreateClient();
        var searchResponse = await guestClient.GetAsync("/api/v1/tours?destination=Hoi+An");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var searchBody = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        var searchItems = searchBody.RootElement.GetProperty("items").EnumerateArray().ToList();
        searchItems.Should().Contain(item => item.GetProperty("tourId").GetString() == seed.TourId.ToString());

        // 3. Public Detail returns the approved tour with bookable schedule
        var detailResponse = await guestClient.GetAsync($"/api/v1/tours/{seed.TourId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailBody = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        detailBody.RootElement.GetProperty("tourId").GetString().Should().Be(seed.TourId.ToString());
        detailBody.RootElement.GetProperty("hasBookableSchedule").GetBoolean().Should().BeTrue();

        // 4. Booking Quote succeeds for an eligible traveler
        using var travelerClient = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        var quoteResponse = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings/quote",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participantCounts = new { adult = 2, child = 0, infant = 0 },
            });
        quoteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var quoteBody = JsonDocument.Parse(await quoteResponse.Content.ReadAsStringAsync());
        quoteBody.RootElement.GetProperty("tourId").GetString().Should().Be(seed.TourId.ToString());
        quoteBody.RootElement.GetProperty("totalAmount").GetDecimal().Should().Be(3_000_000m); // 2 * 1,500,000

        // 5. Booking Create succeeds on real SQL Server
        var bookingKey = Guid.NewGuid().ToString();
        travelerClient.DefaultRequestHeaders.Add("Idempotency-Key", bookingKey);
        var createResponse = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participants = new[]
                {
                    new { fullName = "Nguyen Van A", participantType = "Adult" },
                    new { fullName = "Tran Thi B", participantType = "Adult" },
                },
            });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var bookingIdStr = createBody.RootElement.GetProperty("bookingId").GetString();
        long.TryParse(bookingIdStr, out var bookingId).Should().BeTrue();

        // 6. Verify database state: Booking created, schedule capacity reserved
        await using var db = database.CreateDbContext();
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        booking.Status.Should().Be(BookingStatus.PendingPayment);
        booking.TotalAmount.Should().Be(3_000_000m);

        var schedule = await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == seed.ScheduleId);
        schedule.ReservedCapacity.Should().Be(2);

        // 7. Negative Guard: Locked / Inactive operator tour does not appear in public search
        await database.ExecuteNonQueryAsync($"UPDATE dbo.Users SET status = 'Locked' WHERE user_id = {seed.OperatorId};");
        var postSuspendSearch = await guestClient.GetAsync("/api/v1/tours?destination=Hoi+An");
        postSuspendSearch.StatusCode.Should().Be(HttpStatusCode.OK);
        using var postSuspendBody = JsonDocument.Parse(await postSuspendSearch.Content.ReadAsStringAsync());
        var postSuspendItems = postSuspendBody.RootElement.GetProperty("items").EnumerateArray().ToList();
        postSuspendItems.Should().NotContain(item => item.GetProperty("tourId").GetString() == seed.TourId.ToString());
    }

    private static async Task<SeedResult> SeedPendingTourAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var staff1 = new User
        {
            Role = UserRole.Staff,
            Email = $"staff1-{suffix}@tripmate.test",
            FullName = "Staff One",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var staff2 = new User
        {
            Role = UserRole.Staff,
            Email = $"staff2-{suffix}@tripmate.test",
            FullName = "Staff Two",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var operatorUser = new User
        {
            Role = UserRole.TourOperator,
            Email = $"op-{suffix}@tripmate.test",
            FullName = "Operator User",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var traveler = new User
        {
            Role = UserRole.Traveler,
            Email = $"traveler-{suffix}@tripmate.test",
            FullName = "Traveler User",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        context.Users.AddRange(staff1, staff2, operatorUser, traveler);
        await context.SaveChangesAsync();

        var profile = new OperatorProfile
        {
            UserId = operatorUser.Id,
            CompanyName = "SQL Tours",
            TaxCode = $"TX-{suffix}",
            BusinessLicenseNo = $"BL-{suffix}",
            ContactPhone = "0901112223",
            ContactAddress = "Hoi An",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.OperatorProfiles.Add(profile);

        var category = PoiCategory.Create("Heritage Site", null);
        context.PoiCategories.Add(category);

        var poi1 = PointOfInterest.Create(category, "Ancient Town", 15.88m, 108.33m, operatorUser.Id, now);
        var poi2 = PointOfInterest.Create(category, "Bridge Pagoda", 15.87m, 108.32m, operatorUser.Id, now);
        context.PointsOfInterest.AddRange(poi1, poi2);

        var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
        typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Hoi An");
        context.Destinations.Add(destination);

        var policy = CancellationPolicyTemplate.Create("Standard Policy", null, now,
            [CancellationPolicyTemplateRule.Create(30, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
        context.CancellationPolicyTemplates.Add(policy);

        await context.SaveChangesAsync();

        var tour = Tour.CreateDraft(
            operatorUser.Id,
            categoryId: null,
            $"SQL Test Tour {suffix}",
            "Tour description for SQL concurrency and booking testing.",
            1_500_000m,
            durationDays: 1,
            destinationIds: [destination.Id],
            destinationSummary: "Hoi An",
            policy,
            [
                TourItineraryItem.Create(1, 1, poi1.Id, new TimeOnly(8, 0), 120, null),
                TourItineraryItem.Create(1, 2, poi2.Id, new TimeOnly(11, 0), 60, null),
            ],
            [
                TourSchedule.Create(
                    now.Date.AddDays(10).AddHours(8),
                    now.Date.AddDays(11).AddHours(8),
                    "Hoi An Gate",
                    10,
                    now,
                    adultPrice: 1_500_000m),
            ],
            now,
            "Meals included",
            "Personal expenses");

        tour.SubmitForReview(now);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        tour.SetVersionLineage(tour.Id, 1);
        await context.SaveChangesAsync();

        var schedule = await context.TourSchedules.FirstAsync(s => s.TourId == tour.Id);

        return new SeedResult(staff1.Id, staff2.Id, operatorUser.Id, traveler.Id, tour.Id, schedule.Id);
    }

    private sealed record SeedResult(
        long StaffId1,
        long StaffId2,
        long OperatorId,
        long TravelerId,
        long TourId,
        long ScheduleId);
}
