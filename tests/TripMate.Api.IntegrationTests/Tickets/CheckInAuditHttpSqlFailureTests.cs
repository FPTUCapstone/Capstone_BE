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
public sealed class CheckInAuditHttpSqlFailureTests(ITestOutputHelper output)
{
    private const string Endpoint = "/api/v1/operator/check-in/scan";
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

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

        await AssertMsg127Async(response);
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

        await AssertMsg127Async(response);
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
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, failedBody);
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

    private static HttpTestHost Host(
        string sqlConnection,
        long operatorId,
        string prefix,
        IInterceptor? interceptor = null,
        DateTimeOffset? now = null,
        ICheckInAuditJournal? journal = null)
    {
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
        var factory = new TripMateApiFactory(
            ApiTestAuthenticationMode.HeaderStub,
            sqlConnection,
            dateTimeProviderFactory: _ => new FixedClock(now ?? Departure),
            configureTestServices: journal is null ? null : services =>
            {
                services.RemoveAll<ICheckInAuditJournal>();
                services.AddSingleton(journal);
            },
            dbInterceptor: interceptor,
            configurationOverrides: settings);
        return new HttpTestHost(factory, factory.CreateAuthenticatedClient(
            operatorId, UserRole.TourOperator), redisConnection);
    }

    private static string Prefix(string scenario) =>
        $"tripmate:test:tm89:http-{scenario}:{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> ScanAsync(HttpTestHost host, SeededTicket fixture) =>
        host.Client.PostAsJsonAsync(
            Endpoint,
            new CheckInTravelerCommand(fixture.Seed.ScheduleId, QrContent: fixture.Content));

    private static async Task AssertMsg127Async(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
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
        await using var context = database.CreateDbContext();
        var codec = Codec();
        var issuer = new TicketQrIssuer(
            context, codec, new SqlServerTicketQrPersistenceErrorClassifier());
        TicketQrCode? qr = await issuer.EnsureActiveAsync(
            ticketId, Departure.AddMinutes(-5), CancellationToken.None);
        qr.Should().NotBeNull();
        return new SeededTicket(
            seed, ticketId, qr!.Id, codec.ContentFor(qr.QrToken, qr.Signature));
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

    private sealed class HttpTestHost : IAsyncDisposable
    {
        private readonly TripMateApiFactory _factory;
        private readonly IConnectionMultiplexer _redis;

        public HttpTestHost(TripMateApiFactory factory, HttpClient client, string redisConnection)
        {
            _factory = factory;
            Client = client;
            var options = ConfigurationOptions.Parse(redisConnection);
            options.AllowAdmin = true;
            _redis = ConnectionMultiplexer.Connect(options);
        }

        public HttpClient Client { get; }
        public IDatabase Redis => _redis.GetDatabase();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            await _redis.DisposeAsync();
        }
    }
}