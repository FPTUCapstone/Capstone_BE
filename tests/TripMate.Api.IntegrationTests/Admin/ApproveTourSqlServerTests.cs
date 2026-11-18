using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>
/// TM-106 SQL Server integration suite.
/// Clearly separates required/fixed acceptance tests ([Trait("Classification", "RequiredBehavior"|"VerifiedFix")])
/// from diagnostic defect-reproduction tests ([Trait("Classification", "DefectReproduction")]).
/// Defect-reproduction tests explicitly log diagnostic evidence and MUST NOT be counted as acceptance passes.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ApproveTourSqlServerTests(ITestOutputHelper output)
{
    private static readonly TourReviewChecklistDto ValidChecklist = new(
        ContentCompleteness: true,
        ImageAppropriateness: true,
        PricePlausibility: true,
        PolicyCompliance: true,
        ItineraryFeasibility: true);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "VerifiedFix")]
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

        output.WriteLine(
            $"[VERIFIED_FIX] ConcurrentApprove: Statuses=[{(int)responses[0].StatusCode}, {(int)responses[1].StatusCode}], " +
            $"FinalTourStatus={tour.Status}, ReviewedBy={tour.ReviewedBy}, AuditCount={audits.Count}");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "RequiredBehavior")]
    public async Task RepeatedApproval_AfterCommit_ReturnsConflictMsg133_AndLeavesStateUntouched()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var client = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);

        var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Initial valid approval" });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var checkContext = database.CreateDbContext();
        var tourAfterFirst = await checkContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        var reviewedAtFirst = tourAfterFirst.ReviewedAtUtc;

        var secondResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Duplicate approval attempt" });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var body = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.InvalidState);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("The selected package is no longer in the Pending Approval status.");

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
    [Trait("Classification", "VerifiedFix")]
    public async Task ApprovalPersistenceFailure_RollsBackSqlTransaction_ReturnsMsg127_AndPreservesPendingState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        var failingCommandInterceptor = new FailOnAuditLogInsertCommandInterceptor();
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dbInterceptor: failingCommandInterceptor);

        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);

        var response = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Rollback test on real SQL Server" });

        // Must return HTTP 503 with SRS MSG127 semantics — never a false 409 Conflict
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var rawBody = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(rawBody);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("TripMate is temporarily unable to process your request. Please check your connection and try again.");

        // Confirm interceptor actually intercepted the AuditLog INSERT after Tour UPDATE ran inside the transaction
        failingCommandInterceptor.InterceptedCount.Should().Be(1);

        // Fresh DbContext confirms SQL Server rolled back the UPDATE on commerce.Tours completely
        await using var verificationContext = database.CreateDbContext();
        var tour = await verificationContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Pending);
        tour.ReviewedBy.Should().BeNull();
        tour.ReviewedAtUtc.Should().BeNull();
        tour.PublishedAtUtc.Should().BeNull();

        var audits = await verificationContext.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourApproved)
            .ToListAsync();
        audits.Should().BeEmpty();

        output.WriteLine(
            $"[VERIFIED_FIX] SqlRollbackOnFailure: HttpStatus={(int)response.StatusCode}, ErrorCode=MSG127, " +
            $"InterceptedAuditInserts={failingCommandInterceptor.InterceptedCount}, FinalTourStatus={tour.Status}, " +
            $"ReviewedBy={tour.ReviewedBy?.ToString() ?? "null"}, AuditCount={audits.Count}");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "RequiredBehavior")]
    public async Task ApprovedTour_PublicVisibilityAndBookingFlow_BeforeOperatorLock_SucceedsEndToEnd()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var guestClient = factory.CreateClient();

        // 0. Before approval: Pending tour is hidden from public search and detail
        var preSearchResponse = await guestClient.GetAsync("/api/v1/tours?destination=Hoi+An");
        preSearchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var preSearchBody = JsonDocument.Parse(await preSearchResponse.Content.ReadAsStringAsync());
        preSearchBody.RootElement.GetProperty("items").EnumerateArray().ToList()
            .Should().NotContain(item => item.GetProperty("tourId").GetString() == seed.TourId.ToString());

        var preDetailResponse = await guestClient.GetAsync($"/api/v1/tours/{seed.TourId}");
        preDetailResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 1. Approve the tour via TM-106 API
        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        var approveResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Public Search finds the approved tour
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
        quoteBody.RootElement.GetProperty("totalAmount").GetDecimal().Should().Be(3_000_000m);

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

        // 7. Public visibility guard: Locked operator hides tour from both Public Search and Public Detail
        await database.ExecuteNonQueryAsync($"UPDATE dbo.Users SET status = 'Locked' WHERE user_id = {seed.OperatorId};");
        var postLockSearch = await guestClient.GetAsync("/api/v1/tours?destination=Hoi+An");
        postLockSearch.StatusCode.Should().Be(HttpStatusCode.OK);
        using var postLockBody = JsonDocument.Parse(await postLockSearch.Content.ReadAsStringAsync());
        postLockBody.RootElement.GetProperty("items").EnumerateArray().ToList()
            .Should().NotContain(item => item.GetProperty("tourId").GetString() == seed.TourId.ToString());

        var postLockDetail = await guestClient.GetAsync($"/api/v1/tours/{seed.TourId}");
        postLockDetail.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "DefectReproduction")]
    public async Task Diagnostic_ConcurrentApproveAndReject_ReproducesCrossTaskRaceDefect()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        // Synchronize both Approve (SERIALIZABLE) and Reject (Read Committed, TM-107) at SavingChangesAsync
        // after both have read Status == Pending to deterministically expose the cross-task race defect.
        var barrier = new ApproveRejectRaceBarrier(participants: 2);
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: barrier);

        using var approveClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        using var rejectClient = factory.CreateAuthenticatedClient(seed.StaffId2, UserRole.Staff);

        var approveTask = approveClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Concurrent approval diagnostic" });
        var rejectTask = rejectClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/reject",
            new { reason = "Concurrent rejection diagnostic reason." });

        await Task.WhenAll(approveTask, rejectTask);

        var approveResponse = await approveTask;
        var rejectResponse = await rejectTask;
        var approveBody = await approveResponse.Content.ReadAsStringAsync();
        var rejectBody = await rejectResponse.Content.ReadAsStringAsync();

        await using var verificationContext = database.CreateDbContext();
        var tour = await verificationContext.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        var approvedAudits = await verificationContext.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourApproved)
            .ToListAsync();
        var rejectedAudits = await verificationContext.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourRejected)
            .ToListAsync();

        var bothCommitted = approveResponse.StatusCode == HttpStatusCode.OK
            && rejectResponse.StatusCode == HttpStatusCode.OK;

        output.WriteLine("[DEFECT_REPRODUCED: APPROVE_REJECT_RACE / CROSS_TASK_DEPENDENCY (TM-107)]");
        output.WriteLine($"  Approve HTTP: {(int)approveResponse.StatusCode} {approveResponse.StatusCode} | Body: {approveBody}");
        output.WriteLine($"  Reject  HTTP: {(int)rejectResponse.StatusCode} {rejectResponse.StatusCode} | Body: {rejectBody}");
        output.WriteLine($"  Final Tour Status: {tour.Status}");
        output.WriteLine($"  TourApproved Audit Count: {approvedAudits.Count}");
        output.WriteLine($"  TourRejected Audit Count: {rejectedAudits.Count}");
        output.WriteLine($"  Final ReviewedBy: {tour.ReviewedBy} | Final ReviewedAtUtc: {tour.ReviewedAtUtc:O}");
        output.WriteLine($"  ConflictingDecisionsBothCommitted: {bothCommitted}");

        // Explicitly assert the reproduced defect so this diagnostic cannot silently pass as compliant behavior:
        // RejectTourCommandHandler (TM-107) runs under Read Committed without rowversion or serializable locking,
        // so both decisions commit with HTTP 200 OK and both audit logs are written.
        bothCommitted.Should().BeTrue("Expected deterministic reproduction of cross-task Approve/Reject race defect.");
        approvedAudits.Should().ContainSingle();
        rejectedAudits.Should().ContainSingle();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "VerifiedFix")]
    public async Task UnpublishApprovedTour_TransitionsToInactive_HidesPublicly_BlocksNewBookings_PreservesExistingBookingsAndAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        using var guestClient = factory.CreateClient();
        using var travelerClient = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        // 1. Staff approves Pending tour -> Approved with immediate PublishedAtUtc
        var approveResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist, note = "Gate 1 end-to-end unpublish test" });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Traveler creates an existing booking before takedown
        travelerClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var preBookingResponse = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participants = new[]
                {
                    new { fullName = "Existing Traveler One", participantType = "Adult" },
                    new { fullName = "Existing Traveler Two", participantType = "Adult" },
                },
            });
        preBookingResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var preBookingBody = JsonDocument.Parse(await preBookingResponse.Content.ReadAsStringAsync());
        var existingBookingId = long.Parse(preBookingBody.RootElement.GetProperty("bookingId").GetString()!);

        // 3. Staff unpublishes the publicly active Approved tour (Gate 1)
        using var moderatorClient = factory.CreateAuthenticatedClient(seed.StaffId2, UserRole.Staff);
        var unpublishResponse = await moderatorClient.PostAsync(
            $"/api/v1/admin/tours/{seed.TourId}/unpublish",
            null);
        unpublishResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var unpublishBody = JsonDocument.Parse(await unpublishResponse.Content.ReadAsStringAsync());
        unpublishBody.RootElement.GetProperty("tourId").GetInt64().Should().Be(seed.TourId);
        unpublishBody.RootElement.GetProperty("status").GetString().Should().Be(TourStatus.Inactive.ToString());

        // 4. Duplicate unpublish returns controlled 409 Conflict
        var duplicateUnpublish = await moderatorClient.PostAsync(
            $"/api/v1/admin/tours/{seed.TourId}/unpublish",
            null);
        duplicateUnpublish.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 5. Public Search excludes the tour and Public Detail returns 404
        var postSearch = await guestClient.GetAsync("/api/v1/tours?destination=Hoi+An");
        postSearch.StatusCode.Should().Be(HttpStatusCode.OK);
        using var postSearchBody = JsonDocument.Parse(await postSearch.Content.ReadAsStringAsync());
        postSearchBody.RootElement.GetProperty("items").EnumerateArray().ToList()
            .Should().NotContain(item => item.GetProperty("tourId").GetString() == seed.TourId.ToString());

        var postDetail = await guestClient.GetAsync($"/api/v1/tours/{seed.TourId}");
        postDetail.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 6. New Booking Quote and Booking Create are both rejected for the Inactive tour
        var postQuote = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings/quote",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participantCounts = new { adult = 1, child = 0, infant = 0 },
            });
        postQuote.StatusCode.Should().Be(HttpStatusCode.NotFound);

        travelerClient.DefaultRequestHeaders.Remove("Idempotency-Key");
        travelerClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var postCreate = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participants = new[] { new { fullName = "Blocked Traveler", participantType = "Adult" } },
            });
        postCreate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 7. Verify DB: Tour is Inactive with preserved provenance, existing booking & capacity untouched, accurate audit
        await using var db = database.CreateDbContext();
        var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Inactive);
        tour.ReviewedBy.Should().Be(seed.StaffId1);
        tour.ReviewedAtUtc.Should().NotBeNull();
        tour.PublishedAtUtc.Should().NotBeNull();

        var existingBooking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == existingBookingId);
        existingBooking.Status.Should().Be(BookingStatus.PendingPayment);
        existingBooking.TotalAmount.Should().Be(3_000_000m);

        var schedule = await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == seed.ScheduleId);
        schedule.ReservedCapacity.Should().Be(2);

        var unpublishAudits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourUnpublished)
            .ToListAsync();
        unpublishAudits.Should().ContainSingle();
        unpublishAudits[0].ActorUserId.Should().Be(seed.StaffId2);

        using var beforeJson = JsonDocument.Parse(unpublishAudits[0].BeforeData!);
        beforeJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Approved.ToString());
        using var afterJson = JsonDocument.Parse(unpublishAudits[0].AfterData!);
        afterJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Inactive.ToString());
        afterJson.RootElement.GetProperty("ModeratedBy").GetInt64().Should().Be(seed.StaffId2);
        afterJson.RootElement.GetProperty("VersionNo").GetInt32().Should().Be(1);

        output.WriteLine(
            $"[VERIFIED_FIX: GATE_1_UNPUBLISH_APPROVED] TourStatus={tour.Status}, AuditBefore={unpublishAudits[0].BeforeData}, " +
            $"AuditAfter={unpublishAudits[0].AfterData}, PostQuote={(int)postQuote.StatusCode}, PostCreate={(int)postCreate.StatusCode}, " +
            $"ExistingBookingStatus={existingBooking.Status}, ReservedCapacity={schedule.ReservedCapacity}");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "VerifiedFix")]
    public async Task UnpublishPublishedTour_PreservesExistingBehavior_AndRecordsPublishedBeforeStatus()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        (await staffClient.PostAsJsonAsync($"/api/v1/admin/tours/{seed.TourId}/approve", new { checklist = ValidChecklist }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await staffClient.PostAsync($"/api/v1/admin/tours/{seed.TourId}/publish", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var unpublishResponse = await staffClient.PostAsync($"/api/v1/admin/tours/{seed.TourId}/unpublish", null);
        unpublishResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = database.CreateDbContext();
        var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Inactive);

        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourUnpublished);
        using var beforeJson = JsonDocument.Parse(audit.BeforeData!);
        beforeJson.RootElement.GetProperty("Status").GetString().Should().Be(TourStatus.Published.ToString());

        output.WriteLine(
            $"[VERIFIED_FIX: GATE_1_UNPUBLISH_PUBLISHED] TourStatus={tour.Status}, AuditBefore={audit.BeforeData}, AuditAfter={audit.AfterData}");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "VerifiedFix")]
    public async Task ConcurrentUnpublishRequests_OneSucceedsOneConflicts_AndPersistsSingleAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        using var firstClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        using var secondClient = factory.CreateAuthenticatedClient(seed.StaffId2, UserRole.Staff);

        (await firstClient.PostAsJsonAsync($"/api/v1/admin/tours/{seed.TourId}/approve", new { checklist = ValidChecklist }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var firstTask = firstClient.PostAsync($"/api/v1/admin/tours/{seed.TourId}/unpublish", null);
        var secondTask = secondClient.PostAsync($"/api/v1/admin/tours/{seed.TourId}/unpublish", null);
        var responses = await Task.WhenAll(firstTask, secondTask);

        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.OK);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Conflict);

        await using var db = database.CreateDbContext();
        var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Inactive);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourUnpublished)
            .ToListAsync();
        audits.Should().ContainSingle();

        output.WriteLine(
            $"[VERIFIED_FIX: GATE_1_CONCURRENT_UNPUBLISH] Statuses=[{(int)responses[0].StatusCode}, {(int)responses[1].StatusCode}], " +
            $"FinalStatus={tour.Status}, UnpublishAuditCount={audits.Count}");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "VerifiedFix")]
    public async Task UnpublishPersistenceFailure_RollsBackSqlTransaction_ReturnsMsg127_AndPreservesApprovedState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        var failingCommandInterceptor = new FailOnAuditLogInsertCommandInterceptor { Enabled = false };
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dbInterceptor: failingCommandInterceptor);

        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        (await staffClient.PostAsJsonAsync($"/api/v1/admin/tours/{seed.TourId}/approve", new { checklist = ValidChecklist }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Arm the SQL failure interceptor right before Unpublish so Tour UPDATE runs and AuditLog INSERT fails
        failingCommandInterceptor.Enabled = true;

        var response = await staffClient.PostAsync($"/api/v1/admin/tours/{seed.TourId}/unpublish", null);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        failingCommandInterceptor.InterceptedCount.Should().Be(1);

        await using var db = database.CreateDbContext();
        var tour = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == seed.TourId);
        tour.Status.Should().Be(TourStatus.Approved);

        var unpublishAudits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.AffectedEntityId == seed.TourId && a.ActionType == AuditActionTypes.TourUnpublished)
            .ToListAsync();
        unpublishAudits.Should().BeEmpty();

        output.WriteLine(
            $"[VERIFIED_FIX: GATE_1_UNPUBLISH_ROLLBACK] HttpStatus={(int)response.StatusCode}, ErrorCode=MSG127, " +
            $"InterceptedAuditInserts={failingCommandInterceptor.InterceptedCount}, FinalTourStatus={tour.Status}, UnpublishAuditCount={unpublishAudits.Count}");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "DefectReproduction")]
    public async Task Diagnostic_BookingAfterOperatorLocked_ReproducesCrossTaskBookingDefect()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedPendingTourAsync(database);

        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);

        // 1. Approve tour while Operator is Active
        using var staffClient = factory.CreateAuthenticatedClient(seed.StaffId1, UserRole.Staff);
        var approveResponse = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{seed.TourId}/approve",
            new { checklist = ValidChecklist });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Lock the owning Operator account while keeping schedule, capacity, and Traveler completely valid
        await database.ExecuteNonQueryAsync($"UPDATE dbo.Users SET status = 'Locked' WHERE user_id = {seed.OperatorId};");

        // 3. Confirm Public Search and Public Detail properly hide the tour via TourVisibility.PublicTour
        using var guestClient = factory.CreateClient();
        var searchResponse = await guestClient.GetAsync("/api/v1/tours?destination=Hoi+An");
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var searchBody = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        searchBody.RootElement.GetProperty("items").EnumerateArray().ToList()
            .Should().NotContain(item => item.GetProperty("tourId").GetString() == seed.TourId.ToString());

        var detailResponse = await guestClient.GetAsync($"/api/v1/tours/{seed.TourId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 4. Isolated diagnostic: Attempt valid Quote and Booking Create with valid scheduleId and active Traveler
        using var travelerClient = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        var quoteResponse = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings/quote",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participantCounts = new { adult = 1, child = 0, infant = 0 },
            });
        var quoteRaw = await quoteResponse.Content.ReadAsStringAsync();

        travelerClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var createResponse = await travelerClient.PostAsJsonAsync(
            "/api/v1/traveler/tour-bookings",
            new
            {
                tourScheduleId = seed.ScheduleId.ToString(),
                participants = new[]
                {
                    new { fullName = "Locked Operator Diagnostic Traveler", participantType = "Adult" },
                },
            });
        var createRaw = await createResponse.Content.ReadAsStringAsync();

        await using var db = database.CreateDbContext();
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.TourScheduleId == seed.ScheduleId)
            .ToListAsync();
        var schedule = await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == seed.ScheduleId);

        output.WriteLine("[DEFECT_REPRODUCED: BOOKING_OPERATOR_STATUS_ENFORCEMENT / EXTERNAL_DEPENDENCY]");
        output.WriteLine($"  Public Search Hidden: True | Public Detail HTTP: {(int)detailResponse.StatusCode}");
        output.WriteLine($"  Quote HTTP: {(int)quoteResponse.StatusCode} {quoteResponse.StatusCode} | Body: {quoteRaw}");
        output.WriteLine($"  Create HTTP: {(int)createResponse.StatusCode} {createResponse.StatusCode} | Body: {createRaw}");
        output.WriteLine($"  Persisted Bookings Count: {bookings.Count} | Schedule ReservedCapacity: {schedule.ReservedCapacity}");

        // Reproduce the exact cross-task vulnerability in QuoteTourBookingQueryHandler & CreateTourBookingCommandHandler:
        // Both handlers omit OperatorProfile.User.Status == AccountStatus.Active from IsPubliclyBookableTour.
        quoteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        bookings.Should().ContainSingle();
        schedule.ReservedCapacity.Should().Be(1);
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

    private sealed class ApproveRejectRaceBarrier(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isTourDecision = eventData.Context?.ChangeTracker
                .Entries<Tour>()
                .Any(entry => entry.State == EntityState.Modified
                    && entry.Property(t => t.Status).IsModified) == true;

            if (!isTourDecision)
            {
                return result;
            }

            if (Interlocked.Increment(ref _arrivals) == participants)
            {
                _release.TrySetResult(true);
            }

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private sealed class FailOnAuditLogInsertCommandInterceptor : DbCommandInterceptor
    {
        private int _interceptedCount;

        public bool Enabled { get; set; } = true;

        public int InterceptedCount => Volatile.Read(ref _interceptedCount);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && command.CommandText.Contains("AuditLogs", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _interceptedCount);
                throw new InvalidOperationException("Simulated SQL write failure on AuditLogs INSERT.");
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && command.CommandText.Contains("AuditLogs", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _interceptedCount);
                throw new InvalidOperationException("Simulated SQL write failure on AuditLogs INSERT.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
