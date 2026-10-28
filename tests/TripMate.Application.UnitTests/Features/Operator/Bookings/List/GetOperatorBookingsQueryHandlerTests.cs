using FluentAssertions;

using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Bookings.List;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorBookingTestFixtures;
using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Bookings.List;

public sealed class GetOperatorBookingsQueryHandlerTests
{
    private const long OperatorA = 7;
    private const long OperatorB = 8;

    private static GetOperatorBookingsQueryHandler CreateHandler(TestDbContext db, long? userId, string? role) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role });

    private static GetOperatorBookingsQueryHandler OperatorHandler(TestDbContext db, long operatorId = OperatorA) =>
        CreateHandler(db, operatorId, nameof(UserRole.TourOperator));

    /// <summary>Operator A owns tour 101 / schedule 201; operator B owns tour 102 / schedule 202.</summary>
    private static async Task SeedTwoOperatorsAsync(TestDbContext db)
    {
        await SeedApprovedOperatorAsync(db, OperatorA);
        await SeedApprovedOperatorAsync(db, OperatorB);
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 201, "Tour A");
        await SeedTourWithScheduleAsync(db, OperatorB, 102, 202, "Tour B");
        await SeedBookerAsync(db, 50, "Alice Nguyen", "alice@example.com");
        await SeedBookerAsync(db, 51, "Bob Tran", "bob@example.com");
    }

    // ---- 5a: authorization, isolation, paging, ordering ----

    [Fact]
    public async Task Handle_NonOperatorRole_IsDenied()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);

        var result = await CreateHandler(db, 50, nameof(UserRole.Traveler))
            .Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task Handle_UnapprovedOperator_IsDenied()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        Set(db.OperatorProfiles.Single(profile => profile.UserId == OperatorA),
            nameof(OperatorProfile.ApprovalStatus), OperatorApprovalStatus.PendingApproval);
        await db.SaveChangesAsync();

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task Handle_OnlyReturnsBookingsOnOwnTours_AcrossItemsCountAndSummary()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, quantity: 2, totalAmount: 1_000_000m);
        await SeedBookingAsync(db, 2, 202, 51, quantity: 5, totalAmount: 9_000_000m);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Summary.TotalBookings.Should().Be(1);
        result.Value.Summary.TotalParticipants.Should().Be(2);
        result.Value.Summary.TotalConfirmedAmount.Should().Be(1_000_000m);
        result.Value.Items.Should().ContainSingle().Which.BookingId.Should().Be("1");
    }

    [Fact]
    public async Task Handle_MapsListItemFields()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        var bookedAt = new DateTimeOffset(2026, 10, 20, 3, 15, 0, TimeSpan.Zero);
        await SeedBookingAsync(db, 1, 201, 50, bookingCode: "BK-20261020-0007", status: BookingStatus.Confirmed,
            paymentStatus: PaymentStatus.Paid, quantity: 3, totalAmount: 2_400_000m, discountAmount: 100_000m,
            bookedAtUtc: bookedAt);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.BookingId.Should().Be("1");
        item.BookingCode.Should().Be("BK-20261020-0007");
        item.Tour.TourId.Should().Be("101");
        item.Tour.Title.Should().Be("Tour A");
        item.Schedule.ScheduleId.Should().Be("201");
        item.Schedule.StartAtUtc.Should().Be(DefaultStartUtc);
        item.Schedule.EndAtUtc.Should().Be(DefaultStartUtc.AddHours(10));
        item.Booker.UserId.Should().Be("50");
        item.Booker.FullName.Should().Be("Alice Nguyen");
        item.Booker.Email.Should().Be("alice@example.com");
        item.Quantity.Should().Be(3);
        item.TotalAmount.Should().Be(2_400_000m);
        item.DiscountAmount.Should().Be(100_000m);
        item.Status.Should().Be(BookingStatus.Confirmed);
        item.PaymentStatus.Should().Be(PaymentStatus.Paid);
        item.BookedAtUtc.Should().Be(bookedAt);
    }

    [Fact]
    public async Task Handle_NoBookings_ReturnsEmptyPageWithZeroSummary()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PageNumber.Should().Be(1);
        result.Value.PageSize.Should().Be(20);
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
        result.Value.Summary.Should().Be(new OperatorBookingSummaryDto(0, 0, 0m));
    }

    [Fact]
    public async Task Handle_Paging_ReturnsCorrectTotalsAndEmptyPagePastTheEnd()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        for (long id = 1; id <= 5; id++) await SeedBookingAsync(db, id, 201, 50);

        var handler = OperatorHandler(db);
        var first = await handler.Handle(new GetOperatorBookingsQuery(PageSize: 2), CancellationToken.None);
        var last = await handler.Handle(new GetOperatorBookingsQuery(PageNumber: 3, PageSize: 2), CancellationToken.None);
        var beyond = await handler.Handle(new GetOperatorBookingsQuery(PageNumber: 4, PageSize: 2), CancellationToken.None);

        first.Value.TotalCount.Should().Be(5);
        first.Value.TotalPages.Should().Be(3);
        first.Value.Items.Should().HaveCount(2);
        last.Value.Items.Should().HaveCount(1);
        beyond.Value.Items.Should().BeEmpty();
        beyond.Value.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task Handle_OrdersByBookedAtDescendingThenIdDescending()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        var early = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        await SeedBookingAsync(db, 1, 201, 50, bookedAtUtc: early);
        await SeedBookingAsync(db, 2, 201, 50, bookedAtUtc: late);
        await SeedBookingAsync(db, 3, 201, 50, bookedAtUtc: late);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.Value.Items.Select(item => item.BookingId).Should().Equal("3", "2", "1");
    }

    // ---- 5b: filters, ownership of tourId/scheduleId, Vietnam-time departure range ----

    private static async Task<List<string>> IdsAsync(
        GetOperatorBookingsQueryHandler handler, GetOperatorBookingsQuery query)
    {
        var result = await handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value.Items.Select(item => item.BookingId).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Handle_StatusAndPaymentStatusFilters_NarrowTheList()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, status: BookingStatus.Confirmed, paymentStatus: PaymentStatus.Paid);
        await SeedBookingAsync(db, 2, 201, 50, status: BookingStatus.PendingPayment, paymentStatus: PaymentStatus.Unpaid);
        await SeedBookingAsync(db, 3, 201, 50, status: BookingStatus.Cancelled, paymentStatus: PaymentStatus.Refunded);
        await SeedBookingAsync(db, 4, 201, 50, status: BookingStatus.Expired, paymentStatus: PaymentStatus.Unpaid);
        var handler = OperatorHandler(db);

        (await IdsAsync(handler, new GetOperatorBookingsQuery())).Should().Equal("1", "2", "3", "4");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(Status: BookingStatus.Expired))).Should().Equal("4");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(PaymentStatus: PaymentStatus.Unpaid)))
            .Should().Equal("2", "4");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(
            Status: BookingStatus.PendingPayment, PaymentStatus: PaymentStatus.Unpaid))).Should().Equal("2");
    }

    [Fact]
    public async Task Handle_TourAndScheduleFilters_NarrowWithinOwnTours()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 203, "Tour A");
        await SeedTourWithScheduleAsync(db, OperatorA, 103, 204, "Tour A2");
        await SeedBookingAsync(db, 1, 201, 50);
        await SeedBookingAsync(db, 2, 203, 50);
        await SeedBookingAsync(db, 3, 204, 50);
        var handler = OperatorHandler(db);

        (await IdsAsync(handler, new GetOperatorBookingsQuery(TourId: 101))).Should().Equal("1", "2");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(TourId: 103))).Should().Equal("3");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(ScheduleId: 203))).Should().Equal("2");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(TourId: 101, ScheduleId: 204))).Should().BeEmpty();
    }

    [Theory]
    [InlineData(102L, null)]
    [InlineData(999L, null)]
    [InlineData(null, 202L)]
    [InlineData(null, 999L)]
    public async Task Handle_TourOrScheduleNotOwnedOrMissing_IsForbiddenWithoutLeakingExistence(
        long? tourId, long? scheduleId)
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 2, 202, 51);

        var result = await OperatorHandler(db).Handle(
            new GetOperatorBookingsQuery(TourId: tourId, ScheduleId: scheduleId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorBookingErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_DepartureFromAfterDepartureTo_IsInvalidDateRange()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);

        var result = await OperatorHandler(db).Handle(
            new GetOperatorBookingsQuery(DepartureFrom: new DateOnly(2026, 11, 5), DepartureTo: new DateOnly(2026, 11, 4)),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorBookingErrorCodes.InvalidDateRange);
    }

    [Fact]
    public async Task Handle_DepartureRange_UsesVietnamTimeDayBoundariesInclusive()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        // Vietnam is UTC+7: the 2nd of November local runs from 2026-11-01T17:00Z to 2026-11-02T17:00Z (exclusive).
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 210, startUtc: new DateTimeOffset(2026, 11, 1, 16, 59, 59, TimeSpan.Zero));
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 211, startUtc: new DateTimeOffset(2026, 11, 1, 17, 30, 0, TimeSpan.Zero));
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 212, startUtc: new DateTimeOffset(2026, 11, 2, 16, 59, 59, TimeSpan.Zero));
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 213, startUtc: new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.Zero));
        await SeedBookingAsync(db, 1, 210, 50); // local 1 Nov 23:59:59
        await SeedBookingAsync(db, 2, 211, 50); // local 2 Nov 00:30
        await SeedBookingAsync(db, 3, 212, 50); // local 2 Nov 23:59:59
        await SeedBookingAsync(db, 4, 213, 50); // local 3 Nov 00:00
        var handler = OperatorHandler(db);
        var day = new DateOnly(2026, 11, 2);

        (await IdsAsync(handler, new GetOperatorBookingsQuery(DepartureFrom: day, DepartureTo: day)))
            .Should().Equal("2", "3");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(DepartureFrom: day))).Should().Equal("2", "3", "4");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(DepartureTo: day))).Should().Equal("1", "2", "3");
    }

    // ---- 5c: keyword ----

    [Theory]
    [InlineData("alice", "1")]
    [InlineData("  ALICE nguyen  ", "1")]
    [InlineData("bob@example", "2,3")]
    [InlineData("bk-zz-9", "3")]
    [InlineData("nobody", "")]
    public async Task Handle_Keyword_MatchesBookerNameEmailOrBookingCodeCaseInsensitively(
        string keyword, string expectedIds)
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, bookingCode: "BK-AAA-1");
        await SeedBookingAsync(db, 2, 201, 51, bookingCode: "BK-BBB-2");
        await SeedBookingAsync(db, 3, 201, 51, bookingCode: "BK-ZZ-9");

        var ids = await IdsAsync(OperatorHandler(db), new GetOperatorBookingsQuery(Keyword: keyword));

        ids.Should().Equal(expectedIds.Length == 0 ? [] : expectedIds.Split(','));
    }

    [Fact]
    public async Task Handle_Keyword_DoesNotMatchBookingsOfAnotherOperator()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 202, 50, bookingCode: "BK-OTHER-1");

        var result = await OperatorHandler(db).Handle(
            new GetOperatorBookingsQuery(Keyword: "alice"), CancellationToken.None);

        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_Keyword_TreatsLikeWildcardsAsLiteralText()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookerAsync(db, 52, "100% Fun_Seeker");
        await SeedBookingAsync(db, 1, 201, 50, bookingCode: "BK-AAA-1");
        await SeedBookingAsync(db, 2, 201, 52, bookingCode: "BK-BBB-2");
        var handler = OperatorHandler(db);

        (await IdsAsync(handler, new GetOperatorBookingsQuery(Keyword: "%"))).Should().Equal("2");
        (await IdsAsync(handler, new GetOperatorBookingsQuery(Keyword: "_"))).Should().Equal("2");
        // The InMemory provider does not honour the escaped "[" wildcard; that case is proven on SQL Server.
    }

    // ---- 5d: summary ----

    [Fact]
    public async Task Handle_Summary_CountsEveryStatusButSumsOnlyConfirmedAndCompleted()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, status: BookingStatus.Confirmed, quantity: 2, totalAmount: 1_000_000m);
        await SeedBookingAsync(db, 2, 201, 50, status: BookingStatus.Completed, quantity: 3, totalAmount: 2_000_000m);
        await SeedBookingAsync(db, 3, 201, 50, status: BookingStatus.PendingPayment, paymentStatus: PaymentStatus.Unpaid,
            quantity: 4, totalAmount: 4_000_000m);
        await SeedBookingAsync(db, 4, 201, 50, status: BookingStatus.Cancelled, paymentStatus: PaymentStatus.Refunded,
            quantity: 5, totalAmount: 5_000_000m);
        await SeedBookingAsync(db, 5, 201, 50, status: BookingStatus.Expired, paymentStatus: PaymentStatus.Unpaid,
            quantity: 6, totalAmount: 6_000_000m);
        await SeedBookingAsync(db, 6, 202, 51, status: BookingStatus.Confirmed, quantity: 9, totalAmount: 9_000_000m);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.Value.Summary.TotalBookings.Should().Be(5);
        result.Value.Summary.TotalBookings.Should().Be(result.Value.TotalCount);
        result.Value.Summary.TotalParticipants.Should().Be(5);
        result.Value.Summary.TotalConfirmedAmount.Should().Be(3_000_000m);
    }

    [Fact]
    public async Task Handle_Summary_CoversTheWholeFilteredSetNotJustThePage()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        for (long id = 1; id <= 4; id++)
        {
            await SeedBookingAsync(db, id, 201, 50, quantity: 2, totalAmount: 100_000m);
        }

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(PageSize: 1), CancellationToken.None);

        result.Value.Items.Should().HaveCount(1);
        result.Value.Summary.TotalBookings.Should().Be(4);
        result.Value.Summary.TotalParticipants.Should().Be(8);
        result.Value.Summary.TotalConfirmedAmount.Should().Be(400_000m);
    }

    [Fact]
    public async Task Handle_Summary_FollowsTheAppliedFilters()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, status: BookingStatus.Confirmed, quantity: 2, totalAmount: 1_000_000m);
        await SeedBookingAsync(db, 2, 201, 50, status: BookingStatus.Cancelled, paymentStatus: PaymentStatus.Refunded,
            quantity: 3, totalAmount: 2_000_000m);

        var result = await OperatorHandler(db).Handle(
            new GetOperatorBookingsQuery(Status: BookingStatus.Cancelled), CancellationToken.None);

        result.Value.Summary.Should().Be(new OperatorBookingSummaryDto(1, 0, 0m));
    }

    // ---- 5e: check-in ----

    [Fact]
    public async Task Handle_CheckIn_IsDerivedFromEachBookingsTickets()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        var first = new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);
        await SeedBookingAsync(db, 1, 201, 50); // no ticket
        await SeedBookingAsync(db, 2, 201, 50); // valid ticket
        await SeedBookingAsync(db, 3, 201, 50); // used ticket with a timestamp
        await SeedBookingAsync(db, 4, 201, 50); // used ticket, no timestamp
        await SeedBookingAsync(db, 5, 201, 50); // cancelled ticket
        await SeedBookingAsync(db, 6, 201, 50); // two tickets, earliest timestamp wins
        await SeedBookingAsync(db, 7, 202, 51); // another operator's checked-in booking must not leak
        await SeedTicketAsync(db, 12, 2, TicketStatus.Valid);
        await SeedTicketAsync(db, 13, 3, TicketStatus.Used, checkedInAtUtc: first);
        await SeedTicketAsync(db, 14, 4, TicketStatus.Used);
        await SeedTicketAsync(db, 15, 5, TicketStatus.Cancelled);
        await SeedTicketAsync(db, 16, 6, TicketStatus.Used, checkedInAtUtc: second);
        await SeedTicketAsync(db, 17, 6, TicketStatus.Used, checkedInAtUtc: first);
        await SeedTicketAsync(db, 18, 7, TicketStatus.Used, checkedInAtUtc: first);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        var byId = result.Value.Items.ToDictionary(item => item.BookingId);
        byId.Should().HaveCount(6);
        byId["1"].CheckInStatus.Should().Be(BookingCheckIn.NotCheckedIn);
        byId["1"].CheckedInAtUtc.Should().BeNull();
        byId["2"].CheckInStatus.Should().Be(BookingCheckIn.NotCheckedIn);
        byId["3"].CheckInStatus.Should().Be(BookingCheckIn.CheckedIn);
        byId["3"].CheckedInAtUtc.Should().Be(first);
        byId["4"].CheckInStatus.Should().Be(BookingCheckIn.CheckedIn);
        byId["4"].CheckedInAtUtc.Should().BeNull();
        byId["5"].CheckInStatus.Should().Be(BookingCheckIn.NotCheckedIn);
        byId["6"].CheckInStatus.Should().Be(BookingCheckIn.CheckedIn);
        byId["6"].CheckedInAtUtc.Should().Be(first);
    }
}