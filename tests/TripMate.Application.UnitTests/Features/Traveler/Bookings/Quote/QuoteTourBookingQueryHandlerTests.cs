using FluentAssertions;

using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Quote;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings.Quote;

public sealed class QuoteTourBookingQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ForAdultAndChild_UsesBasePriceFallbackForAdultAndEffectiveAdultForChild()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(operatorId: 2, basePrice: 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), totalCapacity: 10, reservedCapacity: 0);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var handler = new QuoteTourBookingQueryHandler(
            db, new FakeCurrentUserService { UserId = traveler.Id, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });

        var query = new QuoteTourBookingQuery(
            schedule.Id.ToString(), new QuoteParticipantCounts(Adult: 1, Child: 1, Infant: 0), null);
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SubtotalAmount.Should().Be(2_000_000m); // adult 1,000,000 + child (no override) falls back to adult price
        result.Value.TotalAmount.Should().Be(2_000_000m);
        result.Value.RemainingSlots.Should().Be(10);
    }

    [Fact]
    public async Task Handle_WhenScheduleOverridesChildPrice_UsesOverrideInsteadOfAdultFallback()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0, childPrice: 700_000m);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var handler = new QuoteTourBookingQueryHandler(
            db, new FakeCurrentUserService { UserId = traveler.Id, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });

        var query = new QuoteTourBookingQuery(
            schedule.Id.ToString(), new QuoteParticipantCounts(1, 1, 0), null);
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SubtotalAmount.Should().Be(1_700_000m);
    }

    [Fact]
    public async Task Handle_WhenParticipantsExceedRemainingCapacity_ReturnsInsufficientCapacity()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), totalCapacity: 2, reservedCapacity: 1);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var handler = new QuoteTourBookingQueryHandler(
            db, new FakeCurrentUserService { UserId = traveler.Id, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });

        var query = new QuoteTourBookingQuery(schedule.Id.ToString(), new QuoteParticipantCounts(2, 0, 0), null);
        var result = await handler.Handle(query, CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.InsufficientCapacity);
    }

    [Fact]
    public async Task Handle_WhenRegistrationDeadlineHasPassed_ReturnsScheduleNotOpen()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0,
            registrationDeadlineAtUtc: Now.AddDays(-1));
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var handler = new QuoteTourBookingQueryHandler(
            db, new FakeCurrentUserService { UserId = traveler.Id, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });

        var query = new QuoteTourBookingQuery(schedule.Id.ToString(), new QuoteParticipantCounts(1, 0, 0), null);
        var result = await handler.Handle(query, CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.ScheduleNotOpen);
    }

    [Fact]
    public async Task Handle_WithUnknownVoucherCode_ReturnsVoucherNotFound()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var handler = new QuoteTourBookingQueryHandler(
            db, new FakeCurrentUserService { UserId = traveler.Id, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });

        var query = new QuoteTourBookingQuery(schedule.Id.ToString(), new QuoteParticipantCounts(1, 0, 0), "NOPE");
        var result = await handler.Handle(query, CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.VoucherNotFound);
    }

    [Fact]
    public async Task Handle_ForInactiveTravelerAccount_ReturnsAccessRequired()
    {
        await using var db = TestDbContext.Create();
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        traveler.Status = AccountStatus.Locked;
        await db.SaveChangesAsync();
        var handler = new QuoteTourBookingQueryHandler(
            db, new FakeCurrentUserService { UserId = traveler.Id, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });

        var query = new QuoteTourBookingQuery("1", new QuoteParticipantCounts(1, 0, 0), null);
        var result = await handler.Handle(query, CancellationToken.None);

        result.ErrorCode.Should().Be(TourBookingErrorCodes.AccessRequired);
    }
}
