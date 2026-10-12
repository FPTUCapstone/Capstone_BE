using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetSettlementList;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts;

public sealed class GetSettlementPayoutsQueryTests
{
    [Fact]
    public void Validator_AcceptsDefaultQuery()
    {
        var validator = new GetSettlementPayoutsQueryValidator();

        var result = validator.TestValidate(new GetSettlementPayoutsQuery());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Staff_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetSettlementPayoutsQueryHandler(db, new CurrentUser(4, "Staff"));

        var result = await handler.Handle(new GetSettlementPayoutsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("Payouts.Forbidden");
    }

    [Fact]
    public async Task Handle_AdministratorWithNoPayouts_ReturnsEmptySettlementQueue()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetSettlementPayoutsQueryHandler(db, new CurrentUser(1, "Administrator"));

        var result = await handler.Handle(new GetSettlementPayoutsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalAmount.Should().Be(0m);
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;
}
