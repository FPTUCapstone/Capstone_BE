using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts;

public sealed class GetPayoutsQueryTests
{
    [Theory]
    [InlineData("RequestedX")]
    [InlineData("pending")]
    public void Validator_RejectsInvalidStatus(string? status)
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(Status: status));

        result.ShouldHaveValidationErrorFor(query => query.Status);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Requested")]
    [InlineData("Confirmed")]
    [InlineData("Paid")]
    [InlineData("Rejected")]
    public void Validator_AcceptsEveryLifecycleStatus(string status)
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(Status: status));

        result.ShouldNotHaveValidationErrorFor(query => query.Status);
    }

    [Fact]
    public void Validator_RejectsOversizedKeyword()
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(Keyword: new string('a', 201)));

        result.ShouldHaveValidationErrorFor(query => query.Keyword);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    public void Validator_RejectsOutOfRangePaging(int pageNumber, int pageSize)
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery(PageNumber: pageNumber, PageSize: pageSize));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_AcceptsDefaultQuery()
    {
        var validator = new GetPayoutsQueryValidator();

        var result = validator.TestValidate(new GetPayoutsQuery());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(4, "Traveler"));

        var result = await handler.Handle(new GetPayoutsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_InvertedPeriodRange_ReturnsInvalidPeriodRangeWithProposedMsg134()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(1, "Administrator"));

        var result = await handler.Handle(new GetPayoutsQuery(
            PeriodFrom: new DateOnly(2026, 9, 10),
            PeriodTo: new DateOnly(2026, 9, 1)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.InvalidPeriodRange);
        result.ErrorMessage.Should().Be(PayoutErrorCodes.InvalidPeriodRangeMessage);
    }

    [Fact]
    public async Task Handle_NoPayouts_ReturnsSuccessfulEmptyPageWithZeroedSummary()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutsQueryHandler(db, new CurrentUser(1, "Administrator"));

        var result = await handler.Handle(new GetPayoutsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Summary.Should().Be(new PayoutSummaryDto(0, 0m, 0m));
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;
}