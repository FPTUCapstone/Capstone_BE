using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.GetDetail;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts.GetDetail;

public sealed class GetPayoutDetailQueryHandlerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly GetPayoutDetailQueryHandler _handler;

    public GetPayoutDetailQueryHandlerTests()
    {
        _currentUserService.UserId = 1;
        _currentUserService.Role = "Staff";
        _dateTimeProvider.UtcNow = NowUtc;
        _handler = new GetPayoutDetailQueryHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotStaff_ReturnsForbiddenAndWritesNoAuditRow()
    {
        _currentUserService.Role = "Administrator";
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, operatorUser.Id, new DateOnly(2026, 9, 1));

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenPayoutDoesNotExist_ReturnsNotFoundAndWritesNoAuditRow()
    {
        var result = await _handler.Handle(new GetPayoutDetailQuery(999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenPayoutExists_ReturnsDetailAndWritesAuditRow()
    {
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, operatorUser.Id, new DateOnly(2026, 9, 1),
            paymentsReceived: 12_000_000m, commissionAmount: 1_200_000m);

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PayoutId.Should().Be(payout.Id.ToString());
        result.Value.PayoutCode.Should().Be(payout.PayoutCode);
        result.Value.Status.Should().Be(Payout.StatusPendingReview);
        result.Value.GrossRevenue.Should().Be(12_000_000m);
        result.Value.CommissionAmount.Should().Be(1_200_000m);
        result.Value.NetPayoutAmount.Should().Be(10_800_000m);
        result.Value.CommissionRate.Should().Be(0.1m);
        result.Value.Beneficiary.BankName.Should().Be("Vietcombank");
        result.Value.Beneficiary.MaskedAccountNumber.Should().Be("****1234");

        var auditRow = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        auditRow.ActorUserId.Should().Be(1);
        auditRow.AffectedEntityId.Should().Be(payout.Id);
        auditRow.CreatedAtUtc.Should().Be(NowUtc);
    }

    [Fact]
    public async Task Handle_WhenPayoutHasContributingBookings_ListsEachWithPaidAndRefundedAmounts()
    {
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var tour = PayoutTestFixture.CreateTour(1, operatorUser.Id);
        _dbContext.Tours.Add(tour);
        var endAtUtc = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var schedule = PayoutTestFixture.CreateTourSchedule(1, tour.Id, endAtUtc);
        _dbContext.TourSchedules.Add(schedule);
        await _dbContext.SaveChangesAsync();

        var (booking, _) = await PayoutTestFixture.SeedCompletedBookingAsync(
            _dbContext, bookingId: 1, scheduleId: schedule.Id, totalAmount: 5_000_000m,
            succeededAtUtc: endAtUtc.AddDays(-5));

        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, operatorUser.Id, new DateOnly(2026, 9, 1),
            paymentsReceived: 5_000_000m, commissionAmount: 500_000m);
        payout.AddItem(PayoutItem.Create(
            booking.Id, paymentTransactionId: 1, paidAmount: 5_000_000m,
            discountAmount: 0m, commissionRateApplied: 10m, commissionAmount: 500_000m));
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var contributingBooking = result.Value.ContributingBookings.Should().ContainSingle().Subject;
        contributingBooking.BookingId.Should().Be(booking.Id.ToString());
        contributingBooking.BookingCode.Should().Be(booking.BookingCode);
        contributingBooking.PaidAmount.Should().Be(5_000_000m);
        contributingBooking.RefundedAmount.Should().Be(0m);
        contributingBooking.NetAmount.Should().Be(5_000_000m);
    }

    [Fact]
    public async Task Handle_WhenGrossRevenueIsZero_ReturnsZeroCommissionRateInsteadOfDividingByZero()
    {
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, operatorUser.Id, new DateOnly(2026, 9, 1),
            paymentsReceived: 0m, commissionAmount: 0m);

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CommissionRate.Should().Be(0m);
    }
}
