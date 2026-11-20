using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Serilog.Core;
using Serilog.Events;

using StackExchange.Redis;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Application.Features.Operator.CheckIn.Scan;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Security;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Tickets;

[Trait("Category", "SqlServer")]
[Trait("Category", "Redis")]
public sealed class CheckInAuditHttpSqlFailureTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly HashSet<string> _ownedRedisPrefixes = new(StringComparer.Ordinal);

    public Task InitializeAsync() => Task.CompletedTask;

    // xUnit invokes lifetime teardown after the test's await-using hosts have stopped.
    // It records teardown failures alongside, rather than replacing, an assertion failure.
    public async Task DisposeAsync()
    {
        if (_ownedRedisPrefixes.Count == 0) return;
        string connection = Environment.GetEnvironmentVariable(
            RedisTestFixture.ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException("Redis cleanup requires the test connection.");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(
            RedisTestFixture.BuildConfiguration(connection));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IServer server = redis.GetServers().Single();
        IDatabase database = redis.GetDatabase();
        foreach (string prefix in _ownedRedisPrefixes)
        {
            var keys = new List<RedisKey>();
            await foreach (RedisKey key in server.KeysAsync(pattern: prefix + ":*", pageSize: 100)
                .WithCancellation(deadline.Token))
            {
                if (keys.Count == 256)
                    throw new InvalidOperationException("Test namespace cleanup exceeded its key bound.");
                keys.Add(key);
            }
            if (keys.Count > 0)
                await database.KeyDeleteAsync(keys.ToArray()).WaitAsync(deadline.Token);
        }
    }

    private const string Endpoint = "/api/v1/operator/check-in/scan";
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    [RedisTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_AfterOperationFailure_PreservesPrimaryAndReportsCleanupFailure(bool exceedKeyBound)
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        IDatabase redis = fixture.Multiplexer.GetDatabase();
        string prefix = Prefix("cleanup-failure-probe");
        var owner = new CheckInAuditHttpSqlFailureTests(output);
        owner._ownedRedisPrefixes.Add(prefix);
        RedisKey[] keys = Enumerable.Range(0, exceedKeyBound ? 257 : 1)
            .Select(index => (RedisKey)$"{prefix}:probe:{index}").ToArray();
        string otherKey = fixture.CreateUniquePrefix() + ":sentinel";
        var primary = new InvalidOperationException("Injected primary operation failure.");
        var errors = new Xunit.Sdk.ExceptionAggregator();
        try
        {
            await redis.StringSetAsync(otherKey, "test sentinel");
            foreach (RedisKey key in keys) await redis.StringSetAsync(key, "test probe");

            // This is the same aggregator used by xUnit for test execution and lifetime teardown.
            await errors.RunAsync(() => Task.FromException(primary));
            await errors.RunAsync(owner.DisposeAsync);

            Exception recorded = errors.ToException();
            if (exceedKeyBound)
            {
                var aggregate = recorded.Should().BeOfType<AggregateException>().Subject;
                aggregate.InnerExceptions.Should().Contain(primary);
                aggregate.InnerExceptions.Should().Contain(error =>
                    error.Message.Contains("cleanup exceeded its key bound", StringComparison.Ordinal));
            }
            else
            {
                recorded.Should().BeSameAs(primary);
                (await redis.KeyExistsAsync(keys[0])).Should().BeFalse();
            }
            (await redis.KeyExistsAsync(otherKey)).Should().BeTrue();
        }
        finally
        {
            // The deliberately over-bound probe owns these exact keys; never flush the database.
            await redis.KeyDeleteAsync(keys);
        }
    }

    [SqlServerRedisFact]
    public async Task Http_ValidQr_ReturnsSuccessMutatesOnceAndDurablyAdmits()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("valid");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);

        using HttpResponseMessage response = await ScanAsync(host, fixture);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertUsedOnceAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:admissions")).Should().Be(1);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(0);
    }

    [SqlServerRedisFact]
    public async Task Http_ReusedQr_ReturnsConflictAndDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("reused");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);

        (await ScanAsync(host, fixture)).StatusCode.Should().Be(HttpStatusCode.OK);
        using HttpResponseMessage repeated = await ScanAsync(host, fixture);

        repeated.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.AlreadyUsed);
        await AssertUsedOnceAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:admissions")).Should().Be(2);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(1);
    }

    [SqlServerRedisFact]
    public async Task Http_InvalidSignature_ReturnsBadRequestAndDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("invalid-signature");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);
        string invalid = fixture.Content[..^1] + (fixture.Content[^1] == 'A' ? 'B' : 'A');

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: invalid));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.InvalidQr);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(1);
    }

    [SqlServerRedisFact]
    public async Task Http_WrongOperator_ReturnsForbiddenAndDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        long otherOperatorId = await AddApprovedOperatorAsync(database);
        string prefix = Prefix("wrong-operator");
        await using var host = Host(database.ConnectionString, otherOperatorId, prefix);

        using HttpResponseMessage response = await ScanAsync(host, fixture);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.ScheduleForbidden);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(1);
    }

    [SqlServerRedisFact]
    public async Task Http_OutsideDepartureWindow_ReturnsConflictAndDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("outside-window");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix,
            now: Departure.AddDays(-2));

        using HttpResponseMessage response = await ScanAsync(host, fixture);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.DepartureNotToday);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(1);
    }

    [SqlServerRedisFact]
    public async Task Http_AdmissionUnavailable_UsesMsg127AndDoesNotMutateTicket()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        var journal = new ScriptedAuditJournal(CheckInAuditWriteResult.Unavailable);
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId,
            Prefix("admission-unavailable"), journal: journal);

        using HttpResponseMessage response = await ScanAsync(host, fixture);

        await AssertMsg127Async(response, host);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        journal.Events.Should().ContainSingle(e => e.EventType == CheckInAuditEventType.Admission);
    }

    [SqlServerRedisFact]
    public async Task Http_RejectionOutcomeUnavailable_UsesMsg127AndDoesNotReturnBusinessRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        var journal = new ScriptedAuditJournal(
            CheckInAuditWriteResult.Confirmed, CheckInAuditWriteResult.Unavailable);
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId,
            Prefix("outcome-unavailable"), journal: journal);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: "invalid"));

        await AssertMsg127Async(response, host);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        journal.Events.Select(e => e.EventType).Should().Equal(
            CheckInAuditEventType.Admission, CheckInAuditEventType.Rejected);
    }

    [SqlServerRedisFact]
    public async Task Http_ConcurrentScans_ProduceOneSuccessOneConflictAndOneConsumption()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("concurrent");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);

        HttpResponseMessage[] responses = await Task.WhenAll(
            ScanAsync(host, fixture), ScanAsync(host, fixture));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        foreach (HttpResponseMessage response in responses) response.Dispose();
        await AssertUsedOnceAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(1);
    }

    [SqlServerRedisFact]
    public async Task Http_RejectionProjectionDuplicateReplay_HasExactlyOneSqlAuditEffect()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("duplicate-replay");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: "invalid"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string rejectionKey = $"{prefix}:rejections";
        StreamEntry original = (await host.Redis.StreamRangeAsync(rejectionKey)).Should().ContainSingle().Which;
        await WaitForAuditCountAsync(database, 1);

        await host.Redis.StreamAddAsync(rejectionKey, original.Values);
        await WaitForAuditCountAsync(database, 1, stableChecks: 3);

        await using var context = database.CreateDbContext();
        (await context.AuditLogs.CountAsync(a => a.JournalEventId != null)).Should().Be(1);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
    }

    [SqlServerRedisFact]
    public async Task SqlVerificationUnavailable_AfterDurableAdmission_Returns503AndRetryAfterRecoverySucceeds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = $"tripmate:test:tm89:http-sql:{Guid.NewGuid():N}";
        string unavailableConnection = UnavailableConnection(database.ConnectionString);
        await using (var failedHost = Host(unavailableConnection, fixture.Seed.OperatorId, prefix))
        {
            using HttpResponseMessage failed = await failedHost.Client.PostAsJsonAsync(
                Endpoint,
                new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: fixture.Content));

            string failedBody = await failed.Content.ReadAsStringAsync();
            output.WriteLine("SQL unavailable response: status={0}; body={1}",
                (int)failed.StatusCode, failedBody);
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "{0}{1}Server diagnostics:{1}{2}", failedBody, Environment.NewLine, failedHost.Diagnostics);
            JsonElement problem = Problem(failedBody);
            problem.GetProperty("errorCode").GetString().Should().Be(CheckInErrorCodes.Unavailable);
            problem.GetProperty("title").GetString().Should().Be(CheckInErrorCodes.UnavailableMessage);
            await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
            (await failedHost.Redis.StreamLengthAsync($"{prefix}:admissions")).Should().Be(1);
            (await failedHost.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(0,
                "an indeterminate infrastructure attempt must not fabricate a business rejection");
        }

        await using var recoveredHost = Host(database.ConnectionString, fixture.Seed.OperatorId,
            $"tripmate:test:tm89:http-recovered:{Guid.NewGuid():N}");
        using HttpResponseMessage recovered = await recoveredHost.Client.PostAsJsonAsync(
            Endpoint,
            new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: fixture.Content));

        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertUsedOnceAsync(database, fixture.TicketId, fixture.QrId);
    }

    [SqlServerRedisFact]
    public async Task UnexpectedNonSqlFailure_IsNotDisguisedAsMsg127()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = $"tripmate:test:tm89:http-unexpected:{Guid.NewGuid():N}";
        await using var host = Host(
            database.ConnectionString,
            fixture.Seed.OperatorId,
            prefix,
            new ThrowUnexpectedScheduleRead());

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            Endpoint,
            new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: fixture.Content));

        string responseBody = await response.Content.ReadAsStringAsync();
        output.WriteLine("Unexpected failure response: status={0}; body={1}",
            (int)response.StatusCode, responseBody);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, responseBody);
        JsonElement problem = Problem(responseBody);
        problem.TryGetProperty("errorCode", out _).Should().BeFalse();
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        (await host.Redis.StreamLengthAsync($"{prefix}:admissions")).Should().Be(1);
        (await host.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(0);
    }

    [SqlServerRedisFact]
    public async Task Http_WrongSchedule_ReturnsConflictAndProjectsDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        long otherScheduleId = await AddScheduleForSameTourAsync(database, fixture.Seed.ScheduleId, Departure.AddHours(2));
        string prefix = Prefix("wrong-schedule");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(otherScheduleId, QrContent: fixture.Content));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.WrongSchedule);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        await WaitForAuditCountAsync(database, 1);
        await using var context = database.CreateDbContext();
        (await context.AuditLogs.SingleAsync(a => a.JournalEventId != null)).Reason
            .Should().Be(CheckInErrorCodes.WrongSchedule);
    }

    [SqlServerRedisFact]
    public async Task Http_CancelledBooking_ReturnsConflictAndProjectsDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        long bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid, ParticipantType.Adult);
        long ticketId = await seed.AddTicketAsync(bookingId);
        TicketQrCode qr = await IssueQrAsync(database, ticketId);
        await database.ExecuteNonQueryAsync(
            $"UPDATE commerce.Bookings SET status = 'Cancelled' WHERE booking_id = {bookingId};");
        string prefix = Prefix("cancelled-booking");
        await using var host = Host(database.ConnectionString, seed.OperatorId, prefix);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(seed.ScheduleId, QrContent: Codec().ContentFor(qr.QrToken, qr.Signature)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.BookingCancelled);
        await AssertUnusedAsync(database, ticketId, qr.Id);
        await WaitForAuditCountAsync(database, 1);
        await using var context = database.CreateDbContext();
        (await context.AuditLogs.SingleAsync(a => a.JournalEventId != null)).Reason
            .Should().Be(CheckInErrorCodes.BookingCancelled);
    }

    [SqlServerRedisFact]
    public async Task Http_AfterDepartureWindow_ReturnsConflictAndProjectsDurableRejection()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("after-window");
        await using var host = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix,
            now: Departure.AddDays(1));

        using HttpResponseMessage response = await ScanAsync(host, fixture);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")
            .GetString().Should().Be(CheckInErrorCodes.DepartureNotToday);
        await AssertUnusedAsync(database, fixture.TicketId, fixture.QrId);
        await WaitForAuditCountAsync(database, 1);
        await using var context = database.CreateDbContext();
        (await context.AuditLogs.SingleAsync(a => a.JournalEventId != null)).Reason
            .Should().Be(CheckInErrorCodes.DepartureNotToday);
    }

    [SqlServerRedisFact]
    public async Task Http_DepartureCounterReconciliation_MatchesAcrossScanProgressDeparturesAndSql()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        long bookingA = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid,
            ParticipantType.Adult, ParticipantType.Adult, ParticipantType.Child); // 3 pax
        long ticketA = await seed.AddTicketAsync(bookingA);
        TicketQrCode qrA = await IssueQrAsync(database, ticketA);

        long bookingB = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid,
            ParticipantType.Adult, ParticipantType.Child); // 2 pax
        long ticketB = await seed.AddTicketAsync(bookingB);
        TicketQrCode qrB = await IssueQrAsync(database, ticketB);

        long cancelledBooking = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid,
            ParticipantType.Adult, ParticipantType.Adult, ParticipantType.Adult, ParticipantType.Adult); // 4 pax excluded once cancelled
        long cancelledTicket = await seed.AddTicketAsync(cancelledBooking);
        TicketQrCode cancelledQr = await IssueQrAsync(database, cancelledTicket);
        await database.ExecuteNonQueryAsync(
            $"UPDATE commerce.Bookings SET status = 'Cancelled' WHERE booking_id = {cancelledBooking};");

        await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Unpaid, ParticipantType.Adult); // 1 pax excluded

        string bookingCodeB;
        await using (var context = database.CreateDbContext())
        {
            bookingCodeB = await context.Bookings.Where(b => b.Id == bookingB)
                .Select(b => b.BookingCode).SingleAsync();
        }

        string prefix = Prefix("counter-reconciliation");
        await using var host = Host(database.ConnectionString, seed.OperatorId, prefix);

        // Initial progress: 0/5 checked in, 5 waiting.
        DepartureProgressDto initialProgress = (await host.Client.GetFromJsonAsync<DepartureProgressDto>(
            $"/api/v1/operator/check-in/schedules/{seed.ScheduleId}/progress"))!;
        initialProgress.CheckedInParticipants.Should().Be(0);
        initialProgress.TotalEligibleParticipants.Should().Be(5);
        initialProgress.WaitingParticipants.Should().Be(5);

        OperatorDepartureSummaryDto[] initialDepartures = (await host.Client.GetFromJsonAsync<OperatorDepartureSummaryDto[]>(
            "/api/v1/operator/check-in/departures?targetDate=2026-10-30"))!;
        initialDepartures.Should().ContainSingle(d =>
            d.ScheduleId == seed.ScheduleId
            && d.CheckedInParticipants == 0
            && d.TotalEligibleParticipants == 5
            && d.WaitingParticipants == 5);

        // 1. QR scan Booking A (3 pax).
        using HttpResponseMessage scanAResponse = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(seed.ScheduleId, QrContent: Codec().ContentFor(qrA.QrToken, qrA.Signature)));
        scanAResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CheckInResultDto scanA = (await scanAResponse.Content.ReadFromJsonAsync<CheckInResultDto>())!;
        scanA.ParticipantCount.Should().Be(3);
        scanA.Departure.CheckedInParticipants.Should().Be(3);
        scanA.Departure.TotalEligibleParticipants.Should().Be(5);
        scanA.Departure.WaitingParticipants.Should().Be(2);

        // 2. Rejected scans (duplicate A, invalid QR, cancelled booking) must NOT alter counters.
        (await host.Client.PostAsJsonAsync(Endpoint, new CheckInTravelerCommand(seed.ScheduleId,
            QrContent: Codec().ContentFor(qrA.QrToken, qrA.Signature)))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await host.Client.PostAsJsonAsync(Endpoint, new CheckInTravelerCommand(seed.ScheduleId,
            QrContent: "invalid.qr.payload"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await host.Client.PostAsJsonAsync(Endpoint, new CheckInTravelerCommand(seed.ScheduleId,
            QrContent: Codec().ContentFor(cancelledQr.QrToken, cancelledQr.Signature)))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        DepartureProgressDto afterRejects = (await host.Client.GetFromJsonAsync<DepartureProgressDto>(
            $"/api/v1/operator/check-in/schedules/{seed.ScheduleId}/progress"))!;
        afterRejects.CheckedInParticipants.Should().Be(3);
        afterRejects.TotalEligibleParticipants.Should().Be(5);
        afterRejects.WaitingParticipants.Should().Be(2);

        // 3. Manual booking-code check-in for Booking B (2 pax) reconciles to 5/5/0.
        using HttpResponseMessage scanBResponse = await host.Client.PostAsJsonAsync(
            Endpoint, new CheckInTravelerCommand(seed.ScheduleId, BookingCode: bookingCodeB));
        scanBResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CheckInResultDto scanB = (await scanBResponse.Content.ReadFromJsonAsync<CheckInResultDto>())!;
        scanB.ParticipantCount.Should().Be(2);
        scanB.Departure.CheckedInParticipants.Should().Be(5);
        scanB.Departure.TotalEligibleParticipants.Should().Be(5);
        scanB.Departure.WaitingParticipants.Should().Be(0);

        DepartureProgressDto finalProgress = (await host.Client.GetFromJsonAsync<DepartureProgressDto>(
            $"/api/v1/operator/check-in/schedules/{seed.ScheduleId}/progress"))!;
        finalProgress.Should().Be(scanB.Departure);

        OperatorDepartureSummaryDto[] finalDepartures = (await host.Client.GetFromJsonAsync<OperatorDepartureSummaryDto[]>(
            "/api/v1/operator/check-in/departures?targetDate=2026-10-30"))!;
        finalDepartures.Should().ContainSingle(d =>
            d.ScheduleId == seed.ScheduleId
            && d.CheckedInParticipants == 5
            && d.TotalEligibleParticipants == 5
            && d.WaitingParticipants == 0);

        await AssertUsedOnceAsync(database, ticketA, qrA.Id);
        await AssertUsedOnceAsync(database, ticketB, qrB.Id);
        await AssertUnusedAsync(database, cancelledTicket, cancelledQr.Id);
        await WaitForAuditCountAsync(database, 3);
    }

    [SqlServerRedisFact]
    public async Task Http_AdmissionOnlyIndeterminateAttempt_NeverProjectsAsRejectedInSql()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeededTicket fixture = await SeedAsync(database);
        string prefix = Prefix("admission-only");
        string unavailableConnection = UnavailableConnection(database.ConnectionString);

        await using (var failedHost = Host(unavailableConnection, fixture.Seed.OperatorId, prefix))
        {
            using HttpResponseMessage failed = await ScanAsync(failedHost, fixture);
            await AssertMsg127Async(failed, failedHost);
            (await failedHost.Redis.StreamLengthAsync($"{prefix}:admissions")).Should().Be(1);
            (await failedHost.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(0);
        }

        // Start a healthy host sharing the same prefix so the projection worker runs against recovered SQL.
        await using var healthyHost = Host(database.ConnectionString, fixture.Seed.OperatorId, prefix);
        await WaitForAuditCountAsync(database, 0, stableChecks: 5);
        (await healthyHost.Redis.StreamLengthAsync($"{prefix}:admissions")).Should().Be(1);
        (await healthyHost.Redis.StreamLengthAsync($"{prefix}:rejections")).Should().Be(0);
    }

    private HttpTestHost Host(
        string sqlConnection,
        long operatorId,
        string prefix,
        IInterceptor? interceptor = null,
        DateTimeOffset? now = null,
        ICheckInAuditJournal? journal = null)
    {
        if (!prefix.StartsWith("tripmate:test:tm89:http-", StringComparison.Ordinal)
            || !Guid.TryParseExact(prefix[(prefix.LastIndexOf(':') + 1)..], "N", out _))
            throw new ArgumentException("Expected a uniquely owned HTTP test namespace.", nameof(prefix));
        _ownedRedisPrefixes.Add(prefix);
        string redisConnection = Environment.GetEnvironmentVariable(
            RedisTestFixture.ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"Set {RedisTestFixture.ConnectionStringEnvironmentVariable}.");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redisConnection,
            ["CheckInAuditJournal:Enabled"] = "true",
            ["CheckInAuditJournal:AdmissionStreamKey"] = $"{prefix}:admissions",
            ["CheckInAuditJournal:RejectionStreamKey"] = $"{prefix}:rejections",
            ["CheckInAuditJournal:ReceiptKeyPrefix"] = $"{prefix}:receipts",
            ["CheckInAuditJournal:ConsumerGroup"] = $"{prefix}:group",
            ["CheckInAuditJournal:ConsumerName"] = $"{prefix}:consumer",
            ["CheckInAuditJournal:PendingLease"] = "00:00:00.100",
            ["CheckInAuditJournal:PollInterval"] = "00:00:00.050",
            ["CheckInAuditJournal:CommandTimeout"] = "00:00:03",
            ["CheckInAuditJournal:DurabilityTimeout"] = "00:00:02",
        };
        // Keep errors in memory so a failed HTTP assertion can show the server-side exception.
        // The factory deletes its log files on disposal and CI does not retain them.
        var diagnostics = new CapturingLogSink();
        var factory = new TripMateApiFactory(
            ApiTestAuthenticationMode.HeaderStub,
            sqlConnection,
            dateTimeProviderFactory: _ => new FixedClock(now ?? Departure),
            configureTestServices: services =>
            {
                // The host replaces the default logging providers with Serilog, which reads sinks from DI.
                services.AddSingleton<ILogEventSink>(diagnostics);
                if (journal is null)
                    return;

                services.RemoveAll<ICheckInAuditJournal>();
                services.AddSingleton(journal);
            },
            dbInterceptor: interceptor,
            configurationOverrides: settings);
        return new HttpTestHost(factory, factory.CreateAuthenticatedClient(
            operatorId, UserRole.TourOperator), redisConnection, diagnostics);
    }

    private static string Prefix(string scenario) =>
        $"tripmate:test:tm89:http-{scenario}:{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> ScanAsync(HttpTestHost host, SeededTicket fixture) =>
        host.Client.PostAsJsonAsync(
            Endpoint,
            new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: fixture.Content));

    private static async Task AssertMsg127Async(HttpResponseMessage response, HttpTestHost host)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "{0}{1}Server diagnostics:{1}{2}", body, Environment.NewLine, host.Diagnostics);
        JsonElement problem = Problem(body);
        problem.GetProperty("errorCode").GetString().Should().Be(CheckInErrorCodes.Unavailable);
        problem.GetProperty("title").GetString().Should().Be(CheckInErrorCodes.UnavailableMessage);
    }

    private static async Task<long> AddApprovedOperatorAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = $"tm89-other-operator-{Guid.NewGuid():N}@test.local",
            FullName = "TM-89 Other Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "Other Operator",
            TaxCode = $"TAX-{user.Id}",
            BusinessLicenseNo = $"LIC-{user.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task WaitForAuditCountAsync(
        SqlServerTestDatabase database,
        int expected,
        int stableChecks = 1)
    {
        int stable = 0;
        for (int attempt = 0; attempt < 100 && stable < stableChecks; attempt++)
        {
            await using var context = database.CreateDbContext();
            int count = await context.AuditLogs.CountAsync(a => a.JournalEventId != null);
            stable = count == expected ? stable + 1 : 0;
            if (stable < stableChecks) await Task.Delay(50);
        }
        stable.Should().Be(stableChecks);
    }

    private static string UnavailableConnection(string validConnection)
    {
        var builder = new SqlConnectionStringBuilder(validConnection)
        {
            DataSource = "127.0.0.1,1",
            ConnectTimeout = 1,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    private static async Task<SeededTicket> SeedAsync(SqlServerTestDatabase database)
    {
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        long bookingId = await seed.AddBookingAsync(
            BookingStatus.Confirmed, PaymentStatus.Paid, ParticipantType.Adult);
        long ticketId = await seed.AddTicketAsync(bookingId);
        TicketQrCode qr = await IssueQrAsync(database, ticketId);
        return new SeededTicket(
            seed, ticketId, qr.Id, Codec().ContentFor(qr.QrToken, qr.Signature));
    }

    private static async Task<TicketQrCode> IssueQrAsync(SqlServerTestDatabase database, long ticketId)
    {
        await using var context = database.CreateDbContext();
        var issuer = new TicketQrIssuer(
            context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
        TicketQrCode? qr = await issuer.EnsureActiveAsync(
            ticketId, Departure.AddMinutes(-5), CancellationToken.None);
        qr.Should().NotBeNull();
        return qr!;
    }

    private static async Task<long> AddScheduleForSameTourAsync(
        SqlServerTestDatabase database,
        long existingScheduleId,
        DateTimeOffset startAtUtc)
    {
        await using var context = database.CreateDbContext();
        long tourId = await context.TourSchedules.AsNoTracking()
            .Where(s => s.Id == existingScheduleId)
            .Select(s => s.TourId)
            .SingleAsync();
        var schedule = TourSchedule.Create(startAtUtc, startAtUtc.AddHours(6), "Second Gate", 20,
            startAtUtc.AddDays(-30), adultPrice: 1_000_000m);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tourId);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();
        return schedule.Id;
    }

    private static AesGcmHmacTicketQrCodec Codec() => new(Options.Create(new TicketQrOptions
    {
        EncryptionKey = TripMateApiFactory.TicketQrEncryptionKey,
        SigningKey = TripMateApiFactory.TicketQrSigningKey,
    }));

    private static async Task AssertUnusedAsync(
        SqlServerTestDatabase database,
        long ticketId,
        long qrId)
    {
        await using var context = database.CreateDbContext();
        Ticket ticket = await context.Tickets.AsNoTracking().SingleAsync(item => item.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Valid);
        ticket.CheckedInAtUtc.Should().BeNull();
        (await context.TicketQrCodes.AsNoTracking().SingleAsync(item => item.Id == qrId))
            .Status.Should().Be(TicketQrCodeStatus.Active);
        (await context.CheckInLogs.CountAsync(item => item.TicketId == ticketId)).Should().Be(0);
    }

    private static async Task AssertUsedOnceAsync(
        SqlServerTestDatabase database,
        long ticketId,
        long qrId)
    {
        await using var context = database.CreateDbContext();
        Ticket ticket = await context.Tickets.AsNoTracking().SingleAsync(item => item.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Used);
        (await context.TicketQrCodes.AsNoTracking().SingleAsync(item => item.Id == qrId))
            .Status.Should().Be(TicketQrCodeStatus.Consumed);
        (await context.CheckInLogs.CountAsync(item => item.TicketId == ticketId
            && item.ScanResult == CheckInScanResult.Success)).Should().Be(1);
    }

    private static JsonElement Problem(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.Clone();
    }

    private sealed record SeededTicket(
        TicketSqlServerSeed Seed,
        long TicketId,
        long QrId,
        string Content);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ScriptedAuditJournal(params CheckInAuditWriteResult[] results)
        : ICheckInAuditJournal
    {
        private readonly Queue<CheckInAuditWriteResult> _results = new(results);
        public List<CheckInAuditEvent> Events { get; } = [];

        public Task<CheckInAuditWriteResult> AppendAsync(
            CheckInAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ThrowUnexpectedScheduleRead : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                "FROM [commerce].[TourSchedules]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected non-SQL programming failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Records error log entries, including exceptions, for one test host so a failed
    /// assertion can report why the API answered 500 instead of the expected MSG127 response.
    /// </summary>
    private sealed class CapturingLogSink : ILogEventSink
    {
        private const int MaxEntries = 50;
        private const int MaxEntryLength = 8000;
        private readonly ConcurrentQueue<string> _entries = new();

        public string Snapshot() =>
            _entries.IsEmpty ? "(no errors were logged)" : string.Join(Environment.NewLine, _entries);

        public void Emit(LogEvent logEvent)
        {
            // Startup warnings (licensing, EF model hints) would crowd out the failure, so only keep
            // errors and warnings that carry an exception.
            bool relevant = logEvent.Level >= LogEventLevel.Error
                || (logEvent.Level == LogEventLevel.Warning && logEvent.Exception is not null);
            if (!relevant || _entries.Count >= MaxEntries)
                return;

            string entry = logEvent.Exception is null
                ? $"[{logEvent.Level}] {logEvent.RenderMessage()}"
                : $"[{logEvent.Level}] {logEvent.RenderMessage()}{Environment.NewLine}{logEvent.Exception}";
            _entries.Enqueue(entry.Length <= MaxEntryLength ? entry : entry[..MaxEntryLength] + "...");
        }
    }

    private sealed class HttpTestHost : IAsyncDisposable
    {
        private readonly TripMateApiFactory _factory;
        private readonly IConnectionMultiplexer _redis;

        private readonly CapturingLogSink _diagnostics;

        public HttpTestHost(
            TripMateApiFactory factory,
            HttpClient client,
            string redisConnection,
            CapturingLogSink diagnostics)
        {
            _factory = factory;
            _diagnostics = diagnostics;
            Client = client;
            var options = ConfigurationOptions.Parse(redisConnection);
            options.AllowAdmin = true;
            _redis = ConnectionMultiplexer.Connect(options);
        }

        public HttpClient Client { get; }
        public IDatabase Redis => _redis.GetDatabase();

        /// <summary>Errors the host logged so far, for assertion messages.</summary>
        public string Diagnostics => _diagnostics.Snapshot();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            await _redis.DisposeAsync();
        }
    }
}