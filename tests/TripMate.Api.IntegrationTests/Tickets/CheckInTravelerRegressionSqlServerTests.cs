using System.Data.Common;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Application.Features.Operator.CheckIn.Scan;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Security;

namespace TripMate.Api.IntegrationTests.Tickets;

[Trait("Category", "SqlServer")]
public sealed class CheckInTravelerRegressionSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task ActiveQr_ConsumesExactCredential_ReturnsQuantityProgress_AndRejectsRepeat()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, qr) = await SeedAsync(database);

        var result = await ScanAsync(database, seed, qr);

        result.IsSuccess.Should().BeTrue();
        result.Value.ParticipantCount.Should().Be(3);
        result.Value.Departure.CheckedInParticipants.Should().Be(3);
        result.Value.Departure.TotalEligibleParticipants.Should().Be(3);
        result.Value.Departure.WaitingParticipants.Should().Be(0);
        await AssertUsedAsync(database, ticketId, qr.Id);

        var repeat = await ScanAsync(database, seed, qr);
        repeat.ErrorCode.Should().Be(CheckInErrorCodes.AlreadyUsed);
        repeat.ErrorMetadata["checkedInAt"].Should().Be(Departure);
        await AssertUsedAsync(database, ticketId, qr.Id);
    }

    [SqlServerFact]
    public async Task RotatedQr_RejectsOldCredential_WithoutConsumingReplacement()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, oldQr) = await SeedAsync(database);
        TicketQrCode replacement;
        await using (var context = database.CreateDbContext())
        {
            var rotation = await Issuer(context).RotateAsync(ticketId, Departure.AddMinutes(-1), CancellationToken.None);
            rotation.IsSuccess.Should().BeTrue();
            replacement = rotation.Value;
        }

        var result = await ScanAsync(database, seed, oldQr);

        result.ErrorCode.Should().Be(CheckInErrorCodes.InvalidQr);
        await AssertUnusedAsync(database, ticketId);
        await using var verification = database.CreateDbContext();
        (await verification.TicketQrCodes.SingleAsync(q => q.Id == oldQr.Id)).Status.Should().Be(TicketQrCodeStatus.Revoked);
        (await verification.TicketQrCodes.SingleAsync(q => q.Id == replacement.Id)).Status.Should().Be(TicketQrCodeStatus.Active);
        var accepted = await ScanAsync(database, seed, replacement);
        accepted.IsSuccess.Should().BeTrue();
        await AssertUsedAsync(database, ticketId, replacement.Id);
    }

    [SqlServerTheory]
    [InlineData("missing")]
    [InlineData("consumed")]
    [InlineData("timestamp")]
    [InlineData("association")]
    [InlineData("signature")]
    public async Task InvalidCredential_LeavesTicketUnused(string invalidity)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, qr) = await SeedAsync(database);
        var content = Codec().ContentFor(qr.QrToken, qr.Signature);
        if (invalidity == "missing")
            await database.ExecuteNonQueryAsync($"DELETE FROM commerce.TicketQrCodes WHERE qr_code_id = {qr.Id};");
        else if (invalidity == "consumed")
            await database.ExecuteNonQueryAsync($"UPDATE commerce.TicketQrCodes SET status = 'Consumed' WHERE qr_code_id = {qr.Id};");
        else if (invalidity == "timestamp")
            await database.ExecuteNonQueryAsync($"UPDATE commerce.TicketQrCodes SET issued_at = DATEADD(second, 1, issued_at) WHERE qr_code_id = {qr.Id};");
        else if (invalidity == "association")
        {
            var otherTicket = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
            await database.ExecuteNonQueryAsync($"UPDATE commerce.TicketQrCodes SET ticket_id = {otherTicket} WHERE qr_code_id = {qr.Id};");
        }
        else
        {
            var signature = qr.Signature;
            content = Codec().ContentFor(qr.QrToken, (signature[0] == 'A' ? "B" : "A") + signature[1..]);
        }

        await using var context = database.CreateDbContext();
        var result = await Handler(context, seed).Handle(new CheckInTravelerCommand(seed.ScheduleId, QrContent: content), CancellationToken.None);

        result.ErrorCode.Should().Be(CheckInErrorCodes.InvalidQr);
        await AssertUnusedAsync(database, ticketId);
        (await context.AuditLogs.CountAsync(a => a.ActionType == AuditActionTypes.CheckInScanRejected)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task ManualCode_DoesNotRequireScannedCredential_AndConsumesCurrentActiveQr()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, qr) = await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var result = await Handler(context, seed).Handle(
            new CheckInTravelerCommand(seed.ScheduleId, BookingCode: Ticket.CodeFor(await context.Tickets.Where(t => t.Id == ticketId).Select(t => t.BookingId).SingleAsync())),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await AssertUsedAsync(database, ticketId, qr.Id);
    }

    [SqlServerTheory]
    [InlineData("aggregation")]
    [InlineData("save")]
    [InlineData("commit")]
    public async Task RecordingFailure_RollsBackTicketQrAndLog_AndAllowsRetry(string stage)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, qr) = await SeedAsync(database);
        IInterceptor fault = stage switch
        {
            "aggregation" => new FailAggregation(),
            "save" => new FailSave(),
            _ => new FailCommit(),
        };
        await using (var context = database.CreateDbContext(fault))
        {
            Func<Task> action = async () => await Handler(context, seed).Handle(
                new CheckInTravelerCommand(seed.ScheduleId, QrContent: Codec().ContentFor(qr.QrToken, qr.Signature)), CancellationToken.None);
            await action.Should().ThrowAsync<InjectedFailure>();
        }

        await AssertUnusedAsync(database, ticketId);
        await using (var verification = database.CreateDbContext())
            (await verification.TicketQrCodes.SingleAsync(q => q.Id == qr.Id)).Status.Should().Be(TicketQrCodeStatus.Active);

        var retry = await ScanAsync(database, seed, qr);
        retry.IsSuccess.Should().BeTrue();
        retry.Value.Departure.CheckedInParticipants.Should().Be(3);
        await AssertUsedAsync(database, ticketId, qr.Id);
    }

    [SqlServerFact]
    public async Task ConcurrentActualScans_ProduceExactlyOneSuccessfulCheckIn()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, qr) = await SeedAsync(database);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = Enumerable.Range(0, 2).Select(async _ =>
        {
            await ready.Task;
            return await ScanAsync(database, seed, qr);
        }).ToArray();
        ready.SetResult();

        var results = await Task.WhenAll(scans);

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => r.IsFailure).ErrorCode.Should().Be(CheckInErrorCodes.AlreadyUsed);
        await AssertUsedAsync(database, ticketId, qr.Id);
    }

    [SqlServerFact]
    public async Task ConcurrentDifferentTickets_PreserveQuantityProgressOnSameDeparture()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, firstTicketId, firstQr) = await SeedAsync(database);
        var secondTicketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid,
            ParticipantType.Adult, ParticipantType.Child));
        TicketQrCode secondQr;
        await using (var context = database.CreateDbContext())
            secondQr = (await Issuer(context).EnsureActiveAsync(secondTicketId, Departure, CancellationToken.None))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result<CheckInResultDto>> ScanAfterStart(TicketQrCode qr)
        {
            await start.Task;
            return await ScanAsync(database, seed, qr);
        }
        var scans = new[] { ScanAfterStart(firstQr), ScanAfterStart(secondQr) };
        start.SetResult();

        var results = await Task.WhenAll(scans).WaitAsync(TimeSpan.FromSeconds(45));

        results.Should().OnlyContain(r => r.IsSuccess);
        results.Max(r => r.Value.Departure.CheckedInParticipants).Should().Be(5);
        results.Should().OnlyContain(r => r.Value.Departure.TotalEligibleParticipants == 5);
        await AssertUsedAsync(database, firstTicketId, firstQr.Id);
        await AssertUsedAsync(database, secondTicketId, secondQr.Id);
    }

    [SqlServerFact]
    public async Task RefreshCommitsWhileScanWaitsForTransaction_ScanRejectsOldQr()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (seed, ticketId, oldQr) = await SeedAsync(database);
        var barrier = new PauseFirstTicketRead();
        await using var scanContext = database.CreateDbContext(barrier);
        var scan = Handler(scanContext, seed).Handle(
            new CheckInTravelerCommand(seed.ScheduleId, QrContent: Codec().ContentFor(oldQr.QrToken, oldQr.Signature)), CancellationToken.None);
        TicketQrCode replacement;
        try
        {
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var refreshContext = database.CreateDbContext();
            var refreshed = await Issuer(refreshContext).RotateAsync(ticketId, Departure, CancellationToken.None);
            refreshed.IsSuccess.Should().BeTrue();
            replacement = refreshed.Value;
        }
        finally
        {
            barrier.Release.TrySetResult();
        }

        var rejected = await scan.WaitAsync(TimeSpan.FromSeconds(30));
        rejected.ErrorCode.Should().Be(CheckInErrorCodes.InvalidQr);
        await AssertUnusedAsync(database, ticketId);
        (await ScanAsync(database, seed, replacement)).IsSuccess.Should().BeTrue();
        await AssertUsedAsync(database, ticketId, replacement.Id);
    }

    private static async Task<(TicketSqlServerSeed Seed, long TicketId, TicketQrCode Qr)> SeedAsync(SqlServerTestDatabase database)
    {
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid,
            ParticipantType.Adult, ParticipantType.Adult, ParticipantType.Child);
        var ticketId = await seed.AddTicketAsync(bookingId);
        await using var context = database.CreateDbContext();
        // Codec timestamps are whole seconds; persisted issuance may include subsecond precision.
        var qr = await Issuer(context).EnsureActiveAsync(ticketId, Departure.AddMinutes(-5).AddTicks(1234567), CancellationToken.None);
        qr.Should().NotBeNull();
        return (seed, ticketId, qr!);
    }

    private static async Task<Result<CheckInResultDto>> ScanAsync(SqlServerTestDatabase database, TicketSqlServerSeed seed, TicketQrCode qr)
    {
        await using var context = database.CreateDbContext();
        return await Handler(context, seed).Handle(new CheckInTravelerCommand(seed.ScheduleId,
            QrContent: Codec().ContentFor(qr.QrToken, qr.Signature)), CancellationToken.None);
    }

    private static async Task AssertUnusedAsync(SqlServerTestDatabase database, long ticketId)
    {
        await using var context = database.CreateDbContext();
        var ticket = await context.Tickets.SingleAsync(t => t.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Valid);
        ticket.CheckedInAtUtc.Should().BeNull();
        (await context.CheckInLogs.CountAsync(l => l.TicketId == ticketId && l.ScanResult == CheckInScanResult.Success)).Should().Be(0);
    }

    private static async Task AssertUsedAsync(SqlServerTestDatabase database, long ticketId, long qrId)
    {
        await using var context = database.CreateDbContext();
        var ticket = await context.Tickets.SingleAsync(t => t.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Used);
        ticket.CheckedInAtUtc.Should().Be(Departure);
        (await context.TicketQrCodes.SingleAsync(q => q.Id == qrId)).Status.Should().Be(TicketQrCodeStatus.Consumed);
        var log = await context.CheckInLogs.SingleAsync(l => l.TicketId == ticketId && l.ScanResult == CheckInScanResult.Success);
        log.QrCodeId.Should().Be(qrId);
    }

    private static CheckInTravelerCommandHandler Handler(ApplicationDbContext context, TicketSqlServerSeed seed) =>
        new(context, new OperatorIdentity(seed.OperatorId), Codec(), new Clock());

    private static TicketQrIssuer Issuer(ApplicationDbContext context) =>
        new(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());

    private static AesGcmHmacTicketQrCodec Codec() => new(Options.Create(new TicketQrOptions
    {
        EncryptionKey = TripMateApiFactory.TicketQrEncryptionKey,
        SigningKey = TripMateApiFactory.TicketQrSigningKey,
    }));

    private sealed class OperatorIdentity(long id) : ICurrentUserService
    {
        public long? UserId => id;
        public string? Email => "operator@test.local";
        public string? Role => nameof(UserRole.TourOperator);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Departure;
    }

    private sealed class InjectedFailure : Exception;

    private sealed class FailAggregation : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SUM(", StringComparison.OrdinalIgnoreCase))
                throw new InjectedFailure();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new InjectedFailure();
    }

    private sealed class FailCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => throw new InjectedFailure();
    }

    private sealed class PauseFirstTicketRead : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.Transaction is not null && command.CommandText.Contains("FROM [commerce].[Tickets]", StringComparison.Ordinal)
                && Reached.TrySetResult())
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}