using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts;

public sealed class GetPayoutsQueryTests
{
    // Status is deliberately NOT a FluentValidation rule (see GetPayoutsQueryValidator's class
    // comment): the validator must accept any Status value and let GetPayoutsQueryHandler reject
    // it with a stable errorCode. These three cases are therefore handler tests, not validator
    // tests — see Handle_InvalidStatus_ReturnsInvalidStatusWithErrorCode below.
    [Theory]
    [InlineData("PendingReview")]
    [InlineData("ApprovedForSettlement")]
    [InlineData("Rejected")]
    public void Validator_AcceptsStaffVisibleStatus(string status)
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(Status: status));

        result.ShouldNotHaveValidationErrorFor(query => query.Status);
    }

    [Fact]
    public void Validator_RejectsOversizedKeywordWithInvalidFilterErrorCode()
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(Keyword: new string('a', 201)));

        result.ShouldHaveValidationErrorFor(query => query.Keyword)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    public void Validator_RejectsOutOfRangePagingWithInvalidFilterErrorCode(int pageNumber, int pageSize)
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(PageNumber: pageNumber, PageSize: pageSize));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(error => error.ErrorCode == PayoutErrorCodes.InvalidFilter);
    }

    [Fact]
    public void Validator_RejectsPeriodFromOutsideSupportedRangeWithInvalidFilterErrorCode()
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(PeriodFrom: DateOnly.MaxValue));

        result.ShouldHaveValidationErrorFor(query => query.PeriodFrom)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter);
    }

    [Fact]
    public void Validator_AcceptsDefaultQuery()
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NonStaff_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(4, "Traveler"));

        var result = await handler.Handle(new GetPayoutsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("RequestedX")]
    [InlineData("pending")]
    [InlineData("SettlementProcessing")]
    [InlineData("Settled")]
    [InlineData("SettlementFailed")]
    public async Task Handle_InvalidStatus_ReturnsInvalidStatusWithErrorCode(string status)
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(1, "Staff"));

        var result = await handler.Handle(new GetPayoutsQuery(Status: status), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.InvalidStatus);
        result.ErrorMessage.Should().Be(PayoutErrorCodes.InvalidStatusMessage);
    }

    [Fact]
    public async Task Handle_InvertedPeriodRange_ReturnsInvalidPeriodRangeWithProposedMsg134()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(1, "Staff"));

        var result = await handler.Handle(new GetPayoutsQuery(
            PeriodFrom: new DateOnly(2026, 9, 10),
            PeriodTo: new DateOnly(2026, 9, 1)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.InvalidPeriodRange);
        result.ErrorMessage.Should().Be(PayoutErrorCodes.InvalidPeriodRangeMessage);
    }

    [Fact]
    public async Task Handle_NoPayouts_ReturnsSuccessfulEmptyPageWithZeroedFilteredTotals()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(1, "Staff"));

        var result = await handler.Handle(new GetPayoutsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.TotalAmount.Should().Be(0m);
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;
}