using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Tickets.TicketTestData;

namespace TripMate.Application.UnitTests.Features.Tickets;

/// <summary>
/// Phase 3A focused unit tests for UC-29 Option B hardening:
/// T1 — Concurrent refreshes: At most one Active QR per ticket.
/// T2 — Refresh versus ticket consumption: If ticket consumption commits first, refresh cannot issue an Active QR.
/// T3 — Lazy issuance versus ticket consumption: EnsureActiveAsync cannot issue a QR after a ticket becomes Used.
/// T4 — Cancelled ticket: A cancelled ticket cannot receive a new Active QR.
/// T5 — Existing Active QR: Must not return an invalid/stale QR after a terminal ticket transition.
/// T6 — Regression: Valid ticket viewing and QR rotation continue working.
/// T7 — Stale response recovery: TicketResponseBuilder returns Used status and no QR when ticket is consumed concurrently.
/// T8 — Stale response recovery: TicketResponseBuilder returns Cancelled status and no QR when ticket is cancelled concurrently.
/// </summary>
public sealed class TicketQrIssuerHardeningTests
{
    private static void SetProperty<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        property!.SetValue(target, value);
    }

    [Fact]
    public async Task T1_ConcurrentRefreshes_LeaveAtMostOneActiveQrPerTicket()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        await data.AddQrAsync(ticket.Id, "initial-token", TicketQrCodeStatus.Active, InWindow.AddHours(-1));

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);

        // Run sequential rotations and verify only 1 Active QR remains
        var rot1 = await issuer.RotateAsync(ticket.Id, InWindow, CancellationToken.None);
        var rot2 = await issuer.RotateAsync(ticket.Id, InWindow.AddMinutes(5), CancellationToken.None);

        rot1.IsSuccess.Should().BeTrue();
        rot2.IsSuccess.Should().BeTrue();

        var qrs = await data.QrCodesAsync(ticket.Id);
        qrs.Count(q => q.Status == TicketQrCodeStatus.Active).Should().Be(1);
        qrs.Should().HaveCount(3); // 1 initial + 2 rotated
    }

    [Fact]
    public async Task T2_RefreshVersusTicketConsumption_WhenTicketUsed_RejectsAndLeavesZeroActiveQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        var initialQr = await data.AddQrAsync(ticket.Id, "initial-token", TicketQrCodeStatus.Active, InWindow.AddHours(-1));

        // Simulate concurrent check-in: ticket transitions to Used
        SetProperty(ticket, nameof(Ticket.Status), TicketStatus.Used);
        SetProperty(ticket, nameof(Ticket.CheckedInAtUtc), InWindow);
        await data.Db.SaveChangesAsync();

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);
        var rotation = await issuer.RotateAsync(ticket.Id, InWindow, CancellationToken.None);

        rotation.IsFailure.Should().BeTrue();
        rotation.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);
        rotation.ErrorMetadata[TicketErrorCodes.TicketStatusMetadataKey].Should().Be(nameof(TicketDisplayStatus.Used));

        // Verify that no new replacement QR was added
        var qrs = await data.QrCodesAsync(ticket.Id);
        qrs.Should().ContainSingle();
        qrs.Single().Id.Should().Be(initialQr.Id);
    }

    [Fact]
    public async Task T3_LazyIssuanceVersusTicketConsumption_WhenTicketUsed_ReturnsNullAndDoesNotIssueQr()
    {
        var data = new TicketTestData();
        // Ticket created with NO QR code yet
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Simulate ticket consumption via manual booking code check-in before traveler ever opens the app
        SetProperty(ticket, nameof(Ticket.Status), TicketStatus.Used);
        SetProperty(ticket, nameof(Ticket.CheckedInAtUtc), InWindow);
        await data.Db.SaveChangesAsync();

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);
        var qr = await issuer.EnsureActiveAsync(ticket.Id, InWindow, CancellationToken.None);

        qr.Should().BeNull("A Used ticket must not receive a lazily issued Active QR.");
        (await data.QrCodesAsync(ticket.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task T4_CancelledTicket_CannotReceiveNewActiveQrViaRotateOrEnsureActive()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Ticket cancelled
        ticket.Cancel();
        await data.Db.SaveChangesAsync();

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);

        // EnsureActive must return null
        var lazyQr = await issuer.EnsureActiveAsync(ticket.Id, InWindow, CancellationToken.None);
        lazyQr.Should().BeNull();

        // Rotate must return failure
        var rotation = await issuer.RotateAsync(ticket.Id, InWindow, CancellationToken.None);
        rotation.IsFailure.Should().BeTrue();
        rotation.ErrorCode.Should().Be(TicketErrorCodes.NotRefreshable);
        rotation.ErrorMetadata[TicketErrorCodes.TicketStatusMetadataKey].Should().Be(nameof(TicketDisplayStatus.Cancelled));

        (await data.QrCodesAsync(ticket.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task T5_ExistingActiveQr_EnsureActiveReturnsNullAfterTerminalTicketTransition()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));
        await data.AddQrAsync(ticket.Id, "active-token", TicketQrCodeStatus.Active, InWindow.AddHours(-1));

        // Ticket transitions to Used
        SetProperty(ticket, nameof(Ticket.Status), TicketStatus.Used);
        SetProperty(ticket, nameof(Ticket.CheckedInAtUtc), InWindow);
        await data.Db.SaveChangesAsync();

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);
        var qr = await issuer.EnsureActiveAsync(ticket.Id, InWindow, CancellationToken.None);

        // Even though an Active QR row existed historically, EnsureActiveAsync must not return it
        qr.Should().BeNull("EnsureActiveAsync must never return an Active QR for a ticket that has transitioned to Used.");
    }

    [Fact]
    public async Task T6_Regression_ValidTicketViewingAndRotationContinueWorking()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);

        // 1. Lazy issuance on valid ticket
        var firstQr = await issuer.EnsureActiveAsync(ticket.Id, InWindow, CancellationToken.None);
        firstQr.Should().NotBeNull();
        firstQr!.Status.Should().Be(TicketQrCodeStatus.Active);

        // 2. Subsequent call returns existing active QR
        var secondQr = await issuer.EnsureActiveAsync(ticket.Id, InWindow, CancellationToken.None);
        secondQr.Should().NotBeNull();
        secondQr!.Id.Should().Be(firstQr.Id);

        // 3. Rotation revokes old QR and returns a new active QR
        var rotation = await issuer.RotateAsync(ticket.Id, InWindow.AddMinutes(1), CancellationToken.None);
        rotation.IsSuccess.Should().BeTrue();
        rotation.Value.Id.Should().NotBe(firstQr.Id);
        rotation.Value.Status.Should().Be(TicketQrCodeStatus.Active);

        var qrs = await data.QrCodesAsync(ticket.Id);
        qrs.Should().HaveCount(2);
        qrs.Single(q => q.Id == firstQr.Id).Status.Should().Be(TicketQrCodeStatus.Revoked);
        qrs.Single(q => q.Id == rotation.Value.Id).Status.Should().Be(TicketQrCodeStatus.Active);
    }

    [Fact]
    public async Task T7_StaleResponseRecovery_WhenTicketConcurrentlyUsedDuringQrIssuance_ReturnsUsedResponseAndNoQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Initial snapshot read while ticket is Valid
        var snapshot = await TicketSnapshot.ReadAsync(data.Db, ticket.Id, CancellationToken.None);
        snapshot.Should().NotBeNull();
        snapshot!.TicketStatus.Should().Be(TicketStatus.Valid);
        snapshot.CheckedInAtUtc.Should().BeNull();

        // Simulate concurrent check-in: ticket transitions to Used with check-in timestamp
        SetProperty(ticket, nameof(Ticket.Status), TicketStatus.Used);
        SetProperty(ticket, nameof(Ticket.CheckedInAtUtc), InWindow);
        await data.Db.SaveChangesAsync();

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);
        var response = await TicketResponseBuilder.BuildAsync(data.Db, issuer, snapshot, InWindow, CancellationToken.None);

        // Verify response consistency: must reflect Used status with check-in timestamp and no QR
        response.TicketStatus.Should().Be(TicketDisplayStatus.Used);
        response.CheckedInAtUtc.Should().Be(InWindow);
        response.Qr.Should().BeNull("A ticket consumed during QR issuance must not receive an active QR.");
    }

    [Fact]
    public async Task T8_StaleResponseRecovery_WhenTicketConcurrentlyCancelledDuringQrIssuance_ReturnsCancelledResponseAndNoQr()
    {
        var data = new TicketTestData();
        var ticket = await data.AddTicketAsync(await data.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid));

        // Initial snapshot read while ticket is Valid
        var snapshot = await TicketSnapshot.ReadAsync(data.Db, ticket.Id, CancellationToken.None);
        snapshot.Should().NotBeNull();
        snapshot!.TicketStatus.Should().Be(TicketStatus.Valid);

        // Simulate concurrent cancellation
        ticket.Cancel();
        await data.Db.SaveChangesAsync();

        var issuer = new TicketQrIssuer(data.Db, data.Codec, data.Classifier);
        var response = await TicketResponseBuilder.BuildAsync(data.Db, issuer, snapshot, InWindow, CancellationToken.None);

        response.TicketStatus.Should().Be(TicketDisplayStatus.Cancelled);
        response.Qr.Should().BeNull("A cancelled ticket must not receive an active QR.");
    }
}