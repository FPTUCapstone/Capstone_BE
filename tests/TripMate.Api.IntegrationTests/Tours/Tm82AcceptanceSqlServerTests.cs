using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Get;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed partial class OperatorTourPackageSqlServerTests
{
    [SqlServerFact]
    public async Task Tm82_OperatorRejectResubmitAndApprove_PreservesPublicTourUntilStaffDecision()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var path = scenario.OperatorPath;
        var original = await Tm82ExpectAsync(scenario.Traveler.GetAsync(scenario.PublicPath), HttpStatusCode.OK);
        var draft = await Tm82OpenAsync(scenario);
        var replay = await Tm82ExpectAsync(scenario.Operator.PostAsync(path, null), HttpStatusCode.OK);
        replay.GetProperty("id").GetInt64().Should().Be(draft.Id);

        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        (await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG122");
        await Tm82ExpectAsync(scenario.Operator.DeleteAsync(draft.Path), HttpStatusCode.Conflict);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.Conflict);
        Tm82PublicFields(await Tm82ExpectAsync(scenario.Traveler.GetAsync(scenario.PublicPath), HttpStatusCode.OK))
            .Should().BeEquivalentTo(Tm82PublicFields(original));
        await Tm82ExpectAsync(scenario.Traveler.PostAsJsonAsync("/api/v1/traveler/tour-bookings/quote", new
        {
            tourScheduleId = scenario.ScheduleId.ToString(),
            participantCounts = new { adult = 1, child = 0, infant = 0 },
        }), HttpStatusCode.OK);

        var queue = await Tm82ExpectAsync(scenario.Staff.GetAsync("/api/v1/admin/tours/moderation-queue"), HttpStatusCode.OK);
        queue.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kind").GetString())
            .Should().BeEquivalentTo(new[] { "NewSubmission", "DraftVersion" });
        var legacyQueue = await Tm82ExpectAsync(scenario.Staff.GetAsync("/api/v1/admin/tours"), HttpStatusCode.OK);
        legacyQueue.GetRawText().Should().NotContain("DraftVersion").And.NotContain("TM-82 revised title");
        var decision = $"/api/v1/admin/tours/pending-changes/{draft.Id}";
        await Tm82ExpectAsync(scenario.Operator.PostAsync(decision + "/approve", null), HttpStatusCode.Forbidden);
        await Tm82ExpectAsync(scenario.Administrator.PostAsync(decision + "/approve", null), HttpStatusCode.Forbidden);
        var rejected = await Tm82ExpectAsync(scenario.Staff.PostAsJsonAsync(decision + "/reject", new { reason = "Revise itinerary" }), HttpStatusCode.OK);
        rejected.GetProperty("status").GetString().Should().Be("Rejected");
        (await Tm82ExpectAsync(scenario.Staff.PostAsJsonAsync(decision + "/reject", new { reason = "Again" }), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG133");
        Tm82PublicFields(await Tm82ExpectAsync(scenario.Traveler.GetAsync(scenario.PublicPath), HttpStatusCode.OK))
            .Should().BeEquivalentTo(Tm82PublicFields(original));

        var reopened = await Tm82ExpectAsync(scenario.Operator.GetAsync(path + "/current"), HttpStatusCode.OK);
        reopened.GetProperty("rejectionReason").GetString().Should().Be("Revise itinerary");
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        var applied = await Tm82ExpectAsync(scenario.Staff.PostAsync(decision + "/approve", null), HttpStatusCode.OK);
        applied.GetProperty("status").GetString().Should().Be("Applied");
        applied.GetProperty("tourStatus").GetString().Should().Be("Published");
        (await Tm82ExpectAsync(scenario.Staff.PostAsync(decision + "/approve", null), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG133");
        await Tm82ExpectAsync(scenario.Operator.GetAsync(path + "/current"), HttpStatusCode.NotFound);
        (await Tm82ExpectAsync(scenario.Traveler.GetAsync(scenario.PublicPath), HttpStatusCode.OK)).GetRawText()
            .Should().Contain("TM-82 revised title");

        await using var db = scenario.Database.CreateDbContext();
        var tour = await db.Tours.Include(t => t.Schedules).Include(t => t.ItineraryItems).SingleAsync(t => t.Id == scenario.TourId);
        tour.BasePrice.Should().Be(1_200_000m);
        tour.ItineraryItems.Single().Notes.Should().Be("TM-82 revised itinerary");
        tour.Schedules.Single().Id.Should().Be(scenario.ScheduleId);
        tour.Schedules.Single().TotalCapacity.Should().Be(8);
        var actions = await db.AuditLogs.Where(log => log.AffectedEntityId == tour.Id).Select(log => log.ActionType).ToListAsync();
        actions.Count(action => action == AuditActionTypes.TourPendingChangeApproved).Should().Be(1);
        actions.Count(action => action == AuditActionTypes.TourPendingChangeRejected).Should().Be(1);
        var notices = await db.Notifications.Where(notice => notice.RelatedEntityId == draft.Id).Select(notice => notice.Type).ToListAsync();
        notices.Should().BeEquivalentTo(new[] { Notification.TourPendingChangeApprovedType, Notification.TourPendingChangeRejectedType });
    }

    [SqlServerFact]
    public async Task Tm82_RejectRequiresReasonWithSpecifiedStatusAndCode()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        using var response = await scenario.Staff.PostAsJsonAsync($"/api/v1/admin/tours/pending-changes/{draft.Id}/reject", new { reason = " " });
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MSG123");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
    }

    [SqlServerFact]
    public async Task Tm82_OwnershipDiscardAndDirectEditGates()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.OtherOperator.GetAsync(scenario.OperatorPath + "/current"), HttpStatusCode.NotFound);
        await Tm82ExpectAsync(scenario.OtherOperator.PostAsync(scenario.OperatorPath, null), HttpStatusCode.NotFound);
        await Tm82ExpectAsync(scenario.OtherOperator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.NotFound);
        (await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync($"/api/v1/operator/tours/{scenario.TourId}", Tm82SaveBody(draft.Data)), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG110");
        (await Tm82ExpectAsync(scenario.Operator.PostAsync($"/api/v1/operator/tours/{scenario.PendingTourId}/pending-changes", null), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG122");
        await Tm82ExpectAsync(scenario.Operator.DeleteAsync(draft.Path), HttpStatusCode.NoContent);
        var next = await Tm82OpenAsync(scenario);
        next.Id.Should().NotBe(draft.Id);
        next.Data.Title.Should().Be(draft.Data.Title);
    }

    [SqlServerFact]
    public async Task Tm82_GetCurrentReturnsNotFoundWhenNoDraftExists()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        await Tm82ExpectAsync(scenario.Operator.GetAsync(scenario.OperatorPath + "/current"), HttpStatusCode.NotFound);
    }

    [SqlServerFact]
    public async Task Tm82_Br61_SaveRejectsBelowBookedCapacity_AndAcceptsEqualCapacity()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82SetReservedAsync(scenario, 8);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        var lower = draft.Data.Schedules.Single() with { TotalCapacity = 7 };
        (await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data, [lower])), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG143");
    }

    [SqlServerFact]
    public async Task Tm82_Br61_SubmitRechecksCapacityAfterBooking()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82SetReservedAsync(scenario, 9);
        (await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.Conflict))
            .GetRawText().Should().Contain("MSG143");
    }

    [SqlServerFact]
    public async Task Tm82_Br61_ApproveRechecksCapacityAfterSubmission()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        await Tm82SetReservedAsync(scenario, 9);
        using var decision = await scenario.Staff.PostAsync($"/api/v1/admin/tours/pending-changes/{draft.Id}/approve", null);
        await using var db = scenario.Database.CreateDbContext();
        (await db.TourPendingChangeSets.SingleAsync(row => row.Id == draft.Id)).Status.Should().Be(TourPendingChangeStatus.PendingReview);
        (await db.TourSchedules.SingleAsync(row => row.Id == scenario.ScheduleId)).TotalCapacity.Should().Be(12);
        var response = await decision.Content.ReadAsStringAsync();
        response.Should().Contain("MSG143");
        decision.StatusCode.Should().Be(HttpStatusCode.Conflict, response);
    }

    [SqlServerFact]
    public async Task Tm82_PastDepartureRemainsImmutableWhenOperatorProposesChanges()
    {
        await using var scenario = await CreateTm82ScenarioAsync(pastDeparture: true);
        var draft = await Tm82OpenAsync(scenario);
        draft.Data.Schedules.Single().ScheduleId.Should().Be(scenario.ScheduleId);
        var original = draft.Data.Schedules.Single();
        var proposed = original with { MeetingPoint = "New meeting point", TotalCapacity = 8 };
        using var save = await scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data, [proposed]));
        if (save.StatusCode == HttpStatusCode.BadRequest || save.StatusCode == HttpStatusCode.Conflict) return;
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        var saved = await Tm82ExpectAsync(scenario.Operator.GetAsync(scenario.OperatorPath + "/current"), HttpStatusCode.OK);
        var persisted = saved.Deserialize<GetCurrentPendingChangeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Schedules.Single();
        persisted.TotalCapacity.Should().Be(original.TotalCapacity, "a departure already past at draft creation is immutable");
        persisted.MeetingPoint.Should().Be(original.MeetingPoint);
    }

    [SqlServerFact]
    public async Task Tm82_StaffCanReadTheProposedFieldsBeforeDecision()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        var queue = await Tm82ExpectAsync(scenario.Staff.GetAsync("/api/v1/admin/tours/moderation-queue"), HttpStatusCode.OK);
        var approvalItem = queue.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("kind").GetString() == "DraftVersion");
        var detailPath = $"/api/v1/admin/tours/pending-changes/{approvalItem.GetProperty("id").GetInt64()}";
        var detail = await Tm82ExpectAsync(scenario.Staff.GetAsync(detailPath), HttpStatusCode.OK);
        detail.GetProperty("itineraryItems").EnumerateArray().Single().GetProperty("notes").GetString().Should().Be("TM-82 revised itinerary");
        detail.GetProperty("basePrice").GetDecimal().Should().Be(1_200_000m);
        detail.GetProperty("schedules").EnumerateArray().Single().GetProperty("totalCapacity").GetInt32().Should().Be(8);
        await Tm82ExpectAsync(scenario.Operator.GetAsync(detailPath), HttpStatusCode.Forbidden);
        await Tm82ExpectAsync(scenario.Administrator.GetAsync(detailPath), HttpStatusCode.Forbidden);
        await Tm82ExpectAsync(scenario.Staff.GetAsync($"/api/v1/admin/tours/pending-changes/{long.MaxValue}"), HttpStatusCode.NotFound);
    }

    [SqlServerFact]
    public async Task Tm82_PastDepartureSurvivesWhenOmittedFromSaveRequest()
    {
        await using var scenario = await CreateTm82ScenarioAsync(pastDeparture: true);
        var draft = await Tm82OpenAsync(scenario);
        var historical = draft.Data.Schedules.Single();
        var future = historical with
        {
            ScheduleId = null,
            StartAtUtc = historical.StartAtUtc.AddDays(60),
            EndAtUtc = historical.EndAtUtc.AddDays(60),
        };
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data, [future])), HttpStatusCode.OK);
        var saved = await Tm82ExpectAsync(scenario.Operator.GetAsync(scenario.OperatorPath + "/current"), HttpStatusCode.OK);
        var schedules = saved.Deserialize<GetCurrentPendingChangeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Schedules;
        schedules.Should().Contain(historical, "a departure already past when the draft was created must be carried over verbatim even if omitted from the request");
    }

    [SqlServerFact]
    public async Task Tm82_SaveWritesAuditEvent()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await using var db = scenario.Database.CreateDbContext();
        (await db.AuditLogs.CountAsync(log => log.AffectedEntityId == scenario.TourId && log.ActionType == AuditActionTypes.TourPendingChangeSaved)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Tm82_RejectReturnsConfirmationMessage()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        var result = await Tm82ExpectAsync(scenario.Staff.PostAsJsonAsync($"/api/v1/admin/tours/pending-changes/{draft.Id}/reject", new { reason = "Revise itinerary" }), HttpStatusCode.OK);
        result.GetRawText().Should().Contain("MSG145");
    }

    [SqlServerFact]
    public async Task Tm82_ConcurrentCreateReturnsTheSameDraft()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => scenario.Operator.PostAsync(scenario.OperatorPath, null)));
        try
        {
            var statuses = string.Join(", ", responses.Select(response => (int)response.StatusCode));
            await using var db = scenario.Database.CreateDbContext();
            (await db.TourPendingChangeSets.CountAsync(change => change.TourId == scenario.TourId)).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(3, $"the four observed HTTP statuses were {statuses}");
            var payloads = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            payloads.Select(body => ReadLong(body, "id")).Distinct().Should().ContainSingle();
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [SqlServerFact]
    public async Task Tm82_OptionalCategoryCanRoundTripThroughGetAndSave()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data, echoCategory: true)), HttpStatusCode.OK);
    }

    [SqlServerFact]
    public async Task Tm82_ApprovedTourRejectsForeignScheduleAndStillAllowsValidDraftSave()
    {
        await using var scenario = await CreateTm82ScenarioAsync(published: false);
        var draft = await Tm82OpenAsync(scenario);
        await using var db = scenario.Database.CreateDbContext();
        var foreignId = await db.TourSchedules.Where(row => row.TourId == scenario.PendingTourId).Select(row => row.Id).SingleAsync();
        var foreign = draft.Data.Schedules.Single() with { ScheduleId = foreignId };
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data, [foreign])), HttpStatusCode.BadRequest);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
    }

    [SqlServerFact]
    public async Task Tm82_RejectsIncompleteDraftAtSubmission()
    {
        await using var scenario = await CreateTm82ScenarioAsync();
        await using (var db = scenario.Database.CreateDbContext())
        {
            await db.TourItineraryItems.Where(row => row.TourId == scenario.TourId).ExecuteDeleteAsync();
        }
        var draft = await Tm82OpenAsync(scenario);
        var response = await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.BadRequest);
        response.GetRawText().Should().Contain("MSG01");
    }

    [SqlServerFact]
    public async Task Tm82_NotificationFailureAfterCommitKeepsAppliedStateAndAudit()
    {
        var failure = new Tm82DecisionFailure(failNotification: true);
        await using var scenario = await CreateTm82ScenarioAsync(failure: failure);
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        await Tm82RemoveExistingPolicySnapshotAsync(scenario);
        await Tm82ExpectAsync(scenario.Staff.PostAsync($"/api/v1/admin/tours/pending-changes/{draft.Id}/approve", null), HttpStatusCode.OK);
        failure.Attempts.Should().Be(1);
        await using var db = scenario.Database.CreateDbContext();
        (await db.TourPendingChangeSets.SingleAsync(row => row.Id == draft.Id)).Status.Should().Be(TourPendingChangeStatus.Applied);
        (await db.Tours.SingleAsync(row => row.Id == scenario.TourId)).Title.Should().Be("TM-82 revised title");
        (await db.AuditLogs.CountAsync(log => log.AffectedEntityId == scenario.TourId && log.ActionType == AuditActionTypes.TourPendingChangeApproved)).Should().Be(1);
        (await db.Notifications.CountAsync(notice => notice.Type == Notification.TourPendingChangeApprovedType)).Should().Be(0);
    }

    [SqlServerFact]
    public async Task Tm82_DecisionAuditFailureKeepsTourAndPendingState()
    {
        var failure = new Tm82DecisionFailure(failNotification: false);
        await using var scenario = await CreateTm82ScenarioAsync(failure: failure);
        var draft = await Tm82OpenAsync(scenario);
        await Tm82ExpectAsync(scenario.Operator.PutAsJsonAsync(draft.Path, Tm82SaveBody(draft.Data)), HttpStatusCode.OK);
        await Tm82ExpectAsync(scenario.Operator.PostAsync(draft.Path + "/submit", null), HttpStatusCode.OK);
        await Tm82RemoveExistingPolicySnapshotAsync(scenario);
        await Tm82ExpectAsync(scenario.Staff.PostAsync($"/api/v1/admin/tours/pending-changes/{draft.Id}/approve", null), HttpStatusCode.InternalServerError);
        failure.Attempts.Should().Be(1);
        await using var db = scenario.Database.CreateDbContext();
        (await db.TourPendingChangeSets.SingleAsync(row => row.Id == draft.Id)).Status.Should().Be(TourPendingChangeStatus.PendingReview);
        (await db.Tours.SingleAsync(row => row.Id == scenario.TourId)).Title.Should().Be(draft.Data.Title);
        (await db.AuditLogs.CountAsync(log => log.AffectedEntityId == scenario.TourId && log.ActionType == AuditActionTypes.TourPendingChangeApproved)).Should().Be(0);
        (await db.Notifications.CountAsync(notice => notice.Type == Notification.TourPendingChangeApprovedType)).Should().Be(0);
    }

    private static async Task<Tm82Scenario> CreateTm82ScenarioAsync(bool pastDeparture = false, bool published = true,
        SaveChangesInterceptor? failure = null)
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString, saveChangesInterceptor: failure);
        try
        {
            var seed = await SeedAsync(database);
            var staffId = await database.ExecuteScalarAsync<long>("INSERT dbo.Users(role,email,full_name,status) VALUES ('Staff','tm82-staff@test.local','Staff','Active'); SELECT CONVERT(BIGINT,SCOPE_IDENTITY());");
            var travelerId = await database.ExecuteScalarAsync<long>("INSERT dbo.Users(role,email,full_name,status) VALUES ('Traveler','tm82-traveler@test.local','Traveler','Active'); SELECT CONVERT(BIGINT,SCOPE_IDENTITY());");
            var administratorId = await database.ExecuteScalarAsync<long>("INSERT dbo.Users(role,email,full_name,status) VALUES ('Administrator','tm82-admin@test.local','Administrator','Active'); SELECT CONVERT(BIGINT,SCOPE_IDENTITY());");
            var otherId = await database.ExecuteScalarAsync<long>("""
                INSERT dbo.Users(role,email,full_name,status) VALUES ('TourOperator','tm82-other@test.local','Other','Active');
                DECLARE @id BIGINT=SCOPE_IDENTITY();
                INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
                VALUES (@id,'Other Tours','TM82-OTHER','TM82-OTHER','Approved'); SELECT @id;
                """);

            using var operatorSetup = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
            using var created = await PostCreateAsync(operatorSetup, Guid.NewGuid(), seed, [seed.DestinationIds[0]]);
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
            using var pending = await PostCreateAsync(operatorSetup, Guid.NewGuid(), seed, [seed.DestinationIds[1]]);
            pending.StatusCode.Should().Be(HttpStatusCode.Created, await pending.Content.ReadAsStringAsync());
            var pendingTourId = ReadLong(await pending.Content.ReadAsStringAsync(), "tourId");
            await using var db = database.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var liveTour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
            liveTour.SubmitForReview(now.AddMinutes(-4));
            liveTour.Approve(staffId, now.AddMinutes(-3));
            if (published) liveTour.Publish(staffId, now.AddMinutes(-2));
            var firstSubmission = await db.Tours.SingleAsync(tour => tour.Id == pendingTourId);
            firstSubmission.SubmitForReview(now.AddMinutes(-4));
            var schedule = await db.TourSchedules.SingleAsync(item => item.TourId == tourId);
            if (pastDeparture)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.TourSchedules SET start_datetime = DATEADD(day, -40, start_datetime), end_datetime = DATEADD(day, -40, end_datetime) WHERE schedule_id = {schedule.Id}");
            }
            await db.SaveChangesAsync();

            return new Tm82Scenario(database, factory, tourId, pendingTourId, schedule.Id,
                factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator),
                factory.CreateAuthenticatedClient(otherId, UserRole.TourOperator),
                factory.CreateAuthenticatedClient(staffId, UserRole.Staff),
                factory.CreateAuthenticatedClient(administratorId, UserRole.Administrator),
                factory.CreateAuthenticatedClient(travelerId, UserRole.Traveler));
        }
        catch
        {
            await factory.DisposeAsync();
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<(long Id, string Path, GetCurrentPendingChangeResponse Data)> Tm82OpenAsync(Tm82Scenario scenario)
    {
        var created = await Tm82ExpectAsync(scenario.Operator.PostAsync(scenario.OperatorPath, null), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        var current = await Tm82ExpectAsync(scenario.Operator.GetAsync(scenario.OperatorPath + "/current"), HttpStatusCode.OK);
        return (id, $"{scenario.OperatorPath}/{id}", current.Deserialize<GetCurrentPendingChangeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
    }

    private static object Tm82SaveBody(GetCurrentPendingChangeResponse draft, PendingChangeScheduleItem[]? schedules = null, bool echoCategory = false) => new
    {
        CategoryId = echoCategory || draft.CategoryId > 0 ? draft.CategoryId : null,
        Title = "TM-82 revised title",
        Description = "TM-82 revised description",
        BasePrice = 1_200_000m,
        draft.DurationDays,
        draft.DestinationIds,
        draft.CancellationPolicyTemplateId,
        draft.Inclusions,
        draft.Exclusions,
        ItineraryItems = draft.ItineraryItems.Select(item => item with { Notes = "TM-82 revised itinerary" }).ToArray(),
        Schedules = schedules ?? draft.Schedules.Select(schedule => schedule with { TotalCapacity = 8 }).ToArray(),
        draft.TimeZoneId,
    };

    private static async Task Tm82SetReservedAsync(Tm82Scenario scenario, int reservedCapacity)
    {
        await using var db = scenario.Database.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.TourSchedules SET reserved_capacity = {reservedCapacity} WHERE schedule_id = {scenario.ScheduleId}");
    }

    private static async Task Tm82RemoveExistingPolicySnapshotAsync(Tm82Scenario scenario)
    {
        // Isolate the fault-injection scenarios from the existing duplicate cutoff failure on approval.
        await using var db = scenario.Database.CreateDbContext();
        await db.TourCancellationPolicyRules.Where(row => row.TourId == scenario.TourId).ExecuteDeleteAsync();
    }

    private static Dictionary<string, string> Tm82PublicFields(JsonElement response) => response.EnumerateObject()
        .Where(field => field.Name != "asOfUtc").ToDictionary(field => field.Name, field => field.Value.GetRawText());

    private static async Task<JsonElement> Tm82ExpectAsync(Task<HttpResponseMessage> operation, HttpStatusCode expected)
    {
        using var response = await operation;
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        if (string.IsNullOrWhiteSpace(body)) return default;
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private sealed class Tm82DecisionFailure(bool failNotification) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var tracker = eventData.Context!.ChangeTracker;
            var shouldFail = failNotification
                ? tracker.Entries<Notification>().Any(entry => entry.State == EntityState.Added && entry.Entity.Type == Notification.TourPendingChangeApprovedType)
                : tracker.Entries<AuditLog>().Any(entry => entry.State == EntityState.Added && entry.Entity.ActionType == AuditActionTypes.TourPendingChangeApproved);
            if (shouldFail)
            {
                Attempts++;
                throw new InvalidOperationException("TM-82 injected persistence failure.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed record Tm82Scenario(SqlServerTestDatabase Database, TripMateApiFactory Factory,
        long TourId, long PendingTourId, long ScheduleId, HttpClient Operator, HttpClient OtherOperator,
        HttpClient Staff, HttpClient Administrator, HttpClient Traveler) : IAsyncDisposable
    {
        public string OperatorPath => $"/api/v1/operator/tours/{TourId}/pending-changes";
        public string PublicPath => $"/api/v1/tours/{TourId}";

        public async ValueTask DisposeAsync()
        {
            Operator.Dispose();
            OtherOperator.Dispose();
            Staff.Dispose();
            Administrator.Dispose();
            Traveler.Dispose();
            await Factory.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}