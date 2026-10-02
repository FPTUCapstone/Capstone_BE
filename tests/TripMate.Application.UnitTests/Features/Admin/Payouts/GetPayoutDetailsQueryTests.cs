using FluentAssertions;

using FluentValidation.TestHelper;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetDetails;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts;

public sealed class GetPayoutDetailsQueryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_RejectsNonPositivePayoutId(long payoutId)
    {
        var validator = new GetPayoutDetailsQueryValidator();

        var result = validator.TestValidate(new GetPayoutDetailsQuery(payoutId));

        result.ShouldHaveValidationErrorFor(query => query.PayoutId);
    }

    [Fact]
    public void Validator_AcceptsPositivePayoutId()
    {
        var validator = new GetPayoutDetailsQueryValidator();

        var result = validator.TestValidate(new GetPayoutDetailsQuery(1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handle_NonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutDetailsQueryHandler(db, new CurrentUser(4, "Traveler"), new Clock());

        var result = await handler.Handle(new GetPayoutDetailsQuery(1), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_UnknownPayout_ReturnsNotFoundWithLockedMsg128AndNoAuditRow()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutDetailsQueryHandler(db, new CurrentUser(1, "Administrator"), new Clock());

        var result = await handler.Handle(new GetPayoutDetailsQuery(999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
        result.ErrorMessage.Should().Be(PayoutErrorCodes.NotFoundMessage);
        (await db.AuditLogs.ToListAsync()).Should().BeEmpty();
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    }
}