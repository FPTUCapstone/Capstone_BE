using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.Features.Tickets.Get;
using TripMate.Application.Features.Tickets.Refresh;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Security;

namespace TripMate.Api.IntegrationTests.Tickets;

/// <summary>
/// Phase 3A SQL Server Concurrency Tests for UC-29 Option B Hardening:
/// T1 — Concurrent refreshes: At most one Active QR per ticket.
/// T2 — Refresh versus ticket consumption: If ticket consumption commits first, refresh cannot issue an Active QR.
/// T3 — Lazy issuance versus ticket consumption: EnsureActiveAsync cannot issue a QR after a ticket becomes Used.
/// T4 — Cancelled ticket: A cancelled ticket cannot receive a new Active QR.
/// T5 — Deterministic barrier interleaving: Verifies that RotateAsync under serialization detects concurrent consumption.
/// Note: The cross-feature integration test with UC-43 CheckInTravelerCommand is independently implemented in PR B (TM-89 UC-43).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class TicketQrIssuerConcurrencyTests
{
    private const int Concurrency = 2;
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task T1_ConcurrentRefreshes_AlwaysLeaveAtMostOneActiveQrRow()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Seed initial active QR
        await using (var context = database.CreateDbContext())
        {
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            await issuer.EnsureActiveAsync(ticketId, Now, CancellationToken.None);
        }

        // Run 8 concurrent refreshes
        using var start = new SemaphoreSlim(0, Concurrency);
        var tasks = Enumerable.Range(0, Concurrency).Select(async _ =>
        {
            await using var context = database.CreateDbContext();
            await start.WaitAsync();
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            return await issuer.RotateAsync(ticketId, Now, CancellationToken.None);
        }).ToList();

        start.Release(Concurrency);
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsSuccess);
        var activeCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Active';");
        activeCount.Should().Be(1, "Exactly one Active QR code row must exist after concurrent refreshes.");
    }

    [SqlServerFact]
    public async Task T2_RefreshVersusTicketConsumption_WhenTicketUsed_AbortsAndLeavesZeroActiveQr()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Issue initial active QR
        await using (var context = database.CreateDbContext())
        {
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            await issuer.EnsureActiveAsync(ticketId, Now, CancellationToken.None);
        }

        // Simulate ticket consumed at boarding gate
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.Tickets
            SET status = 'Used', checked_in_at = '{Now.UtcDateTime:s}'
            WHERE ticket_id = {ticketId};
            UPDATE commerce.TicketQrCodes
            SET status = 'Consumed', consumed_at = '{Now.UtcDateTime:s}'
            WHERE ticket_id = {ticketId} AND status = 'Active';
            """);

        // Attempt refresh on the Used ticket
        await using (var context = database.CreateDbContext())
        {
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            var rotation = await issuer.RotateAsync(ticketId, Now, CancellationToken.None);

            rotation.IsFailure.Should().BeTrue();
            rotation.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);
            rotation.ErrorMetadata[TicketErrorCodes.TicketStatusMetadataKey].Should().Be(nameof(TicketDisplayStatus.Used));
        }

        var activeCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Active';");
        activeCount.Should().Be(0, "A Used ticket must never retain or receive an Active QR code.");
    }

    [SqlServerFact]
    public async Task T3_LazyIssuanceVersusTicketConsumption_WhenTicketUsed_ReturnsNullAndLeavesZeroActiveQr()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        // Ticket created with NO QR code
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Ticket consumed via manual booking code check-in before traveler ever views the ticket
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.Tickets
            SET status = 'Used', checked_in_at = '{Now.UtcDateTime:s}'
            WHERE ticket_id = {ticketId};
            """);

        // Attempt lazy issuance
        await using (var context = database.CreateDbContext())
        {
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            var qr = await issuer.EnsureActiveAsync(ticketId, Now, CancellationToken.None);

            qr.Should().BeNull();
        }

        var qrCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId};");
        qrCount.Should().Be(0, "No QR record should be created for a Used ticket.");
    }

    [SqlServerFact]
    public async Task T4_CancelledTicket_CannotReceiveActiveQrViaRotateOrEnsureActive()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Cancel ticket
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.Tickets SET status = 'Cancelled' WHERE ticket_id = {ticketId};
            UPDATE commerce.Bookings SET status = 'Cancelled' WHERE booking_id = (SELECT booking_id FROM commerce.Tickets WHERE ticket_id = {ticketId});
            """);

        await using (var context = database.CreateDbContext())
        {
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());

            var qr = await issuer.EnsureActiveAsync(ticketId, Now, CancellationToken.None);
            qr.Should().BeNull();

            var rotation = await issuer.RotateAsync(ticketId, Now, CancellationToken.None);
            rotation.IsFailure.Should().BeTrue();
            rotation.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);
            rotation.ErrorMetadata[TicketErrorCodes.TicketStatusMetadataKey].Should().Be(nameof(TicketDisplayStatus.Cancelled));
        }

        var activeCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Active';");
        activeCount.Should().Be(0);
    }

    [SqlServerFact]
    public async Task T5_DeterministicInterleaving_BarrierSynchronization_PreventsActiveQrForUsedTicket()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ticketId = await seed.AddTicketAsync(await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Initial Active QR
        await using (var context = database.CreateDbContext())
        {
            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            await issuer.EnsureActiveAsync(ticketId, Now, CancellationToken.None);
        }

        var barrier = new Barrier(2);

        // Thread 1: Refresh command handler
        var refreshTask = Task.Run(async () =>
        {
            await using var context = database.CreateDbContext();
            // Read snapshot outside transaction (as RefreshTicketQrCommandHandler does)
            var snapshot = await TicketSnapshot.ReadAsync(context, ticketId, CancellationToken.None);
            snapshot.Should().NotBeNull();
            snapshot!.TicketStatus.Should().Be(TicketStatus.Valid);

            // Synchronize: wait for Thread 2 to consume ticket
            barrier.SignalAndWait();
            // Give Thread 2 a moment to commit consumption
            await Task.Delay(50);

            var issuer = new TicketQrIssuer(context, Codec(), new SqlServerTicketQrPersistenceErrorClassifier());
            return await issuer.RotateAsync(ticketId, Now, CancellationToken.None);
        });

        // Thread 2: Simulates UC-43 Check-In committing Ticket.Status = Used
        var checkInTask = Task.Run(async () =>
        {
            barrier.SignalAndWait(); // Wait for Thread 1 to read snapshot
            await using var context = database.CreateDbContext();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE commerce.Tickets SET status = 'Used', checked_in_at = {Now.UtcDateTime} WHERE ticket_id = {ticketId};");
        });

        await Task.WhenAll(refreshTask, checkInTask);
        var rotationResult = await refreshTask;

        // RotateAsync under serializable check must detect ticket.Status == Used and reject
        rotationResult.IsFailure.Should().BeTrue("RotateAsync must detect ticket consumption under serialization.");
        rotationResult.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);

        var activeCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM commerce.TicketQrCodes WHERE ticket_id = {ticketId} AND status = 'Active';");
        activeCount.Should().Be(0, "Zero Active QR codes must remain when ticket was consumed during refresh.");
    }

    private static AesGcmHmacTicketQrCodec Codec() => new(Options.Create(new TicketQrOptions
    {
        EncryptionKey = TripMateApiFactory.TicketQrEncryptionKey,
        SigningKey = TripMateApiFactory.TicketQrSigningKey,
    }));
}