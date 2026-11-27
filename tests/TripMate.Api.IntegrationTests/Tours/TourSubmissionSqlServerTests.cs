using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed partial class OperatorTourPackageSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Submit_WhenPaymentChannelIsDisabled_LeavesTheDraftAndReportsTheBlocker()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        await using (var db = database.CreateDbContext())
        {
            var channel = await db.OperatorPaymentChannels.SingleAsync(channel => channel.TourOperatorUserId == seed.OperatorId);
            channel.Disable(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed, [seed.DestinationIds[0]]);
        created.StatusCode.Should().Be(HttpStatusCode.Created, "a payment channel is not required to save a draft");
        long tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
        await using (var db = database.CreateDbContext())
        {
            var tour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
            db.TourMedia.Add(TourMedia.Create(tour, "tm83/blocked-cover", "https://cdn.example.test/blocked.jpg", null, "Cover", 1, true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var detail = await client.GetAsync($"/api/v1/operator/tours/{tourId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        detailJson.RootElement.GetProperty("canSubmitForReview").GetBoolean().Should().BeFalse();
        detailJson.RootElement.GetProperty("reviewBlockers").EnumerateArray().Select(blocker => blocker.GetString())
            .Should().Equal(TourReviewBlockers.PaymentChannelNotReady);

        using var submitted = await client.PostAsync($"/api/v1/operator/tours/{tourId}/submit-review", null);
        submitted.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await using var verification = database.CreateDbContext();
        var draft = await verification.Tours.AsNoTracking().SingleAsync(tour => tour.Id == tourId);
        draft.Status.Should().Be(TourStatus.Draft);
        draft.SubmittedAtUtc.Should().BeNull();
        (await verification.AuditLogs.CountAsync(log => log.AffectedEntityId == tourId &&
            log.ActionType == TripMate.Domain.Common.AuditActionTypes.TourSubmitForReview)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Submit_NoBodyCompatibility_ValidationOwnershipAndPublicationGate()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed, [seed.DestinationIds[0]]);
        long tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
        long scheduleId;
        await using (var db = database.CreateDbContext())
        {
            var tour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
            db.TourMedia.Add(TourMedia.Create(tour, "tm83/cover", "https://cdn.example.test/tm83.jpg", null, "Cover", 1, true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            scheduleId = await db.TourSchedules.Where(schedule => schedule.TourId == tourId).Select(schedule => schedule.Id).SingleAsync();
        }
        using var invalid = await client.PostAsJsonAsync($"/api/v1/operator/tours/{tourId}/submit-review", new { submissionNote = new string('x', 4001) });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using (var db = database.CreateDbContext())
        {
            var tour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
            tour.Status.Should().Be(TourStatus.Draft);
            tour.SubmittedAtUtc.Should().BeNull();
            tour.SubmissionNote.Should().BeNull();
            (await db.AuditLogs.CountAsync()).Should().Be(0);
        }
        var otherId = await database.ExecuteScalarAsync<long>("""
            INSERT dbo.Users(role,email,full_name,status) VALUES ('TourOperator','tm83-other@test.local','Other','Active');
            DECLARE @id BIGINT=SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES (@id,'Other Tours','TM83-OTHER','TM83-OTHER','Approved'); SELECT @id;
            """);
        using var other = factory.CreateAuthenticatedClient(otherId, UserRole.TourOperator);
        using var foreign = await other.PostAsync($"/api/v1/operator/tours/{tourId}/submit-review", null);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var submitted = await client.PostAsync($"/api/v1/operator/tours/{tourId}/submit-review", null);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var travelerId = await database.ExecuteScalarAsync<long>("INSERT dbo.Users(role,email,full_name,status) VALUES ('Traveler','tm83-traveler@test.local','Traveler','Active'); SELECT CONVERT(BIGINT,SCOPE_IDENTITY());");
        using var traveler = factory.CreateAuthenticatedClient(travelerId, UserRole.Traveler);
        foreach (var status in new[] { TourStatus.Pending, TourStatus.Approved, TourStatus.Published })
        {
            if (status != TourStatus.Pending)
            {
                await using var db = database.CreateDbContext();
                var tour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
                if (status == TourStatus.Approved) tour.Approve(seed.OperatorId, DateTimeOffset.UtcNow);
                else tour.Publish(seed.OperatorId, DateTimeOffset.UtcNow.AddSeconds(-1));
                await db.SaveChangesAsync();
            }
            using var detail = await traveler.GetAsync($"/api/v1/tours/{tourId}");
            detail.StatusCode.Should().Be(status == TourStatus.Published ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            using var search = await traveler.GetAsync("/api/v1/tours?keyword=SQL%20Server%20integration%20tour");
            search.StatusCode.Should().Be(HttpStatusCode.OK);
            using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
            searchJson.RootElement.GetProperty("totalCount").GetInt64().Should().Be(status == TourStatus.Published ? 1 : 0);
            if (status != TourStatus.Published)
            {
                using var bookingRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/traveler/tour-bookings")
                {
                    Content = JsonContent.Create(new
                    {
                        tourScheduleId = scheduleId.ToString(),
                        participants = new[] { new { fullName = "Test Traveler", participantType = "Adult" } }
                    })
                };
                bookingRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                using var booking = await traveler.SendAsync(bookingRequest);
                booking.StatusCode.Should().Be(HttpStatusCode.NotFound);
                await using var verification = database.CreateDbContext();
                (await verification.Bookings.CountAsync()).Should().Be(0);
            }
            using var quote = await traveler.PostAsJsonAsync("/api/v1/traveler/tour-bookings/quote", new
            {
                tourScheduleId = scheduleId.ToString(),
                participantCounts = new { adult = 1, child = 0, infant = 0 }
            });
            quote.StatusCode.Should().Be(status == TourStatus.Published ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Submit_ConcurrentRequests_CreateOneSubmissionAndExposeStaffQueueAndDetail()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed, [seed.DestinationIds[0]]);
        var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
        await using (var db = database.CreateDbContext())
        {
            var tour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
            db.TourMedia.Add(TourMedia.Create(tour, "tm83/cover", "https://cdn.example.test/tm83.jpg", null, "Cover", 1, true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync(
            $"/api/v1/operator/tours/{tourId}/submit-review", new { submissionNote = "  Please review  " })));
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(3);
        using var success = JsonDocument.Parse(await responses.Single(response => response.IsSuccessStatusCode).Content.ReadAsStringAsync());
        success.RootElement.GetProperty("submittedAtUtc").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
        foreach (var response in responses) response.Dispose();
        await using (var db = database.CreateDbContext())
        {
            var tour = await db.Tours.SingleAsync(tour => tour.Id == tourId);
            tour.SubmissionNote.Should().Be("Please review");
            tour.SubmittedAtUtc.Should().NotBeNull();
            (await db.AuditLogs.CountAsync(log => log.AffectedEntityId == tourId && log.ActionType == TripMate.Domain.Common.AuditActionTypes.TourSubmitForReview)).Should().Be(1);
        }
        using var operatorDetail = await client.GetAsync($"/api/v1/operator/tours/{tourId}");
        using var operatorJson = JsonDocument.Parse(await operatorDetail.Content.ReadAsStringAsync());
        operatorJson.RootElement.GetProperty("submissionNote").GetString().Should().Be("Please review");
        operatorJson.RootElement.GetProperty("canSubmitForReview").GetBoolean().Should().BeFalse();
        var staffId = await database.ExecuteScalarAsync<long>("INSERT dbo.Users(role,email,full_name,status) VALUES ('Staff','tm83-staff@test.local','Staff','Active'); SELECT CONVERT(BIGINT,SCOPE_IDENTITY());");
        using var staff = factory.CreateAuthenticatedClient(staffId, UserRole.Staff);
        using var queue = await staff.GetAsync("/api/v1/admin/tours");
        queue.StatusCode.Should().Be(HttpStatusCode.OK);
        (await queue.Content.ReadAsStringAsync()).Should().Contain("Please review");
        using var staffDetail = await staff.GetAsync($"/api/v1/admin/tours/{tourId}");
        staffDetail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staffDetail.Content.ReadAsStringAsync()).Should().Contain("Please review").And.Contain("submittedAtUtc");
        using var admin = factory.CreateAuthenticatedClient(staffId, UserRole.Administrator);
        using var denied = await admin.GetAsync($"/api/v1/admin/tours/{tourId}");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var publicDetail = await client.GetAsync($"/api/v1/tours/{tourId}");
        publicDetail.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}