using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings.Create;

public sealed class CreateTourBookingCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly TourBookingOptions Options = new() { PaymentExpiry = TimeSpan.FromMinutes(15), MaxUnpaidBookingsPerTraveler = 3 };

    [Fact]
    public async Task Handle_ForValidRequest_CreatesBookingAndReservesCapacity()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), totalCapacity: 10, reservedCapacity: 0);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var handler = NewHandler(db, traveler.Id);

        var result = await handler.Handle(Command(schedule.Id, [Participant("Nguyen Van A")]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(BookingStatus.PendingPayment));
        result.Value.TotalAmount.Should().Be(1_000_000m);
        result.Value.IsReplay.Should().BeFalse();
        (await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id)).ReservedCapacity.Should().Be(1);
        (await db.Bookings.CountAsync()).Should().Be(1);
        (await db.BookingParticipants.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_CalledTwiceWithSameKeyAndPayload_ReplaysWithoutDoubleReservingCapacity()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var key = Guid.NewGuid();
        var command = Command(schedule.Id, [Participant("Nguyen Van A")]) with { IdempotencyKey = key };

        var first = await NewHandler(db, traveler.Id).Handle(command, CancellationToken.None);
        var second = await NewHandler(db, traveler.Id).Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.IsReplay.Should().BeTrue();
        second.Value.BookingId.Should().Be(first.Value.BookingId);
        (await db.Bookings.CountAsync()).Should().Be(1);
        (await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id)).ReservedCapacity.Should().Be(1);
    }

    [Fact]
    public async Task Handle_SameKeyWithDifferentPayload_ReturnsPayloadMismatch()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var key = Guid.NewGuid();
        await NewHandler(db, traveler.Id).Handle(
            Command(schedule.Id, [Participant("Nguyen Van A")]) with { IdempotencyKey = key }, CancellationToken.None);

        var result = await NewHandler(db, traveler.Id).Handle(
            Command(schedule.Id, [Participant("Different Name")]) with { IdempotencyKey = key }, CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.IdempotencyKeyPayloadMismatch);
        (await db.Bookings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenParticipantsExceedRemainingCapacity_ReturnsInsufficientCapacityAndCreatesNothing()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), totalCapacity: 1, reservedCapacity: 1);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var result = await NewHandler(db, traveler.Id).Handle(
            Command(schedule.Id, [Participant("A"), Participant("B")]), CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.InsufficientCapacity);
        (await db.Bookings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenTravelerAlreadyHasMaxUnpaidBookings_ReturnsTooManyUnpaidBookings()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        for (var i = 0; i < Options.MaxUnpaidBookingsPerTraveler; i++)
        {
            await NewHandler(db, traveler.Id).Handle(
                Command(schedule.Id, [Participant($"P{i}")]) with { IdempotencyKey = Guid.NewGuid() },
                CancellationToken.None);
        }

        var result = await NewHandler(db, traveler.Id).Handle(
            Command(schedule.Id, [Participant("Overflow")]), CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.TooManyUnpaidBookings);
    }

    [Fact]
    public async Task Handle_WithValidVoucher_IncrementsUsageAndRecordsRedemption()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0);
        db.TourSchedules.Add(schedule);
        var voucher = TourBookingTestData.SeedVoucher("SAVE10", VoucherDiscountType.Percentage, 10m, 500_000m, 0m,
            usageLimit: 100, usageLimitPerUser: 1, Now.AddDays(-1), Now.AddDays(30), usedCount: 0, [tour]);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();

        var result = await NewHandler(db, traveler.Id).Handle(
            Command(schedule.Id, [Participant("A")], "SAVE10"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DiscountAmount.Should().Be(100_000m);
        result.Value.TotalAmount.Should().Be(900_000m);
        (await db.Vouchers.AsNoTracking().SingleAsync()).UsedCount.Should().Be(1);
        (await db.VoucherRedemptions.CountAsync()).Should().Be(1);
    }

    private static CreateTourBookingCommandHandler NewHandler(TestDbContext db, long travelerId) =>
        new(db, new FakeCurrentUserService { UserId = travelerId, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now }, Options);

    private static CreateTourBookingCommand Command(
        long scheduleId, IReadOnlyList<CreateTourBookingParticipantRequest> participants, string? voucherCode = null) =>
        new(scheduleId.ToString(), participants, voucherCode, Guid.NewGuid());

    private static CreateTourBookingParticipantRequest Participant(string fullName) =>
        new(fullName, ParticipantType.Adult, null, null, null, null, null);
}
