using System.Text.Json;

using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.PayoutSettlements.GetList;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements;

public sealed class GetPayoutSettlementsQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ApprovedForSettlement")]
    [InlineData("SettlementProcessing")]
    [InlineData("Settled")]
    [InlineData("SettlementFailed")]
    public void Validator_AcceptsEverySettlementScopeStatus(string status)
    {
        new GetPayoutSettlementsQueryValidator()
            .TestValidate(new GetPayoutSettlementsQuery(Status: status))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsOversizedKeywordAndBadPagingWithInvalidFilterErrorCode()
    {
        var validator = new GetPayoutSettlementsQueryValidator();

        validator.TestValidate(new GetPayoutSettlementsQuery(Keyword: new string('a', 201)))
            .ShouldHaveValidationErrorFor(query => query.Keyword)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter);
        var paging = validator.TestValidate(new GetPayoutSettlementsQuery(PageNumber: 0, PageSize: 101));
        paging.IsValid.Should().BeFalse();
        paging.Errors.Should().OnlyContain(error => error.ErrorCode == PayoutErrorCodes.InvalidFilter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Staff")]
    [InlineData("Traveler")]
    [InlineData("TourOperator")]
    [InlineData("administrator")]
    public async Task Handle_AnyoneButAnAdministrator_IsForbidden(string? role)
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutSettlementsQueryHandler(db, new CurrentUser(role is null ? null : 1, role));

        var result = await handler.Handle(new GetPayoutSettlementsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("PendingReview")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    [InlineData("approvedforsettlement")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Handle_StatusOutsideTheSettlementScope_IsRejectedWithInvalidStatus(string? status)
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var result = await handler.Handle(new GetPayoutSettlementsQuery(Status: status), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.InvalidStatus);
    }

    [Fact]
    public async Task Handle_InvertedPeriodRange_ReturnsInvalidPeriodRange()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var result = await handler.Handle(
            new GetPayoutSettlementsQuery(PeriodFrom: new DateOnly(2026, 9, 10), PeriodTo: new DateOnly(2026, 9, 1)),
            CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutErrorCodes.InvalidPeriodRange);
    }

    [Fact]
    public async Task Handle_Default_ReturnsOnlyApprovedForSettlementWithFilteredTotals()
    {
        await using var db = TestDbContext.Create();
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 7);
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 8);
        var first = await SeedAsync(db, 7, Payout.StatusApprovedForSettlement, new DateOnly(2026, 9, 1), 10_000_000m);
        var second = await SeedAsync(db, 8, Payout.StatusApprovedForSettlement, new DateOnly(2026, 10, 1), 5_000_000m);
        await SeedAsync(db, 7, Payout.StatusSettled, new DateOnly(2026, 8, 1), 3_000_000m);
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var result = await handler.Handle(new GetPayoutSettlementsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.PayoutId).Should().Equal(second.Id.ToString(), first.Id.ToString());
        result.Value.Items.Should().OnlyContain(item => item.Status == Payout.StatusApprovedForSettlement);
        result.Value.TotalCount.Should().Be(2);
        result.Value.TotalAmount.Should().Be(first.NetAmount + second.NetAmount);
        result.Value.PageNumber.Should().Be(1);
        result.Value.PageSize.Should().Be(20);
    }

    [Theory]
    [InlineData(Payout.StatusSettlementProcessing)]
    [InlineData(Payout.StatusSettled)]
    [InlineData(Payout.StatusSettlementFailed)]
    public async Task Handle_ExplicitSettlementStatus_ReturnsOnlyThatStatus(string status)
    {
        await using var db = TestDbContext.Create();
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 7);
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 8);
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 9);
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 10);
        var wanted = await SeedAsync(db, 7, status, new DateOnly(2026, 9, 1), 4_000_000m);
        await SeedAsync(db, 8, Payout.StatusApprovedForSettlement, new DateOnly(2026, 9, 1), 4_000_000m);
        await PayoutTestFixture.SeedPendingReviewPayoutAsync(db, 9, new DateOnly(2026, 9, 1));
        await SeedAsync(db, 10, status == Payout.StatusSettled ? Payout.StatusSettlementFailed : Payout.StatusSettled,
            new DateOnly(2026, 9, 1), 4_000_000m);
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var result = await handler.Handle(new GetPayoutSettlementsQuery(Status: status), CancellationToken.None);

        result.Value.Items.Select(item => item.PayoutId).Should().Equal(wanted.Id.ToString());
    }

    [Fact]
    public async Task Handle_PeriodFilterUsesOverlapSemantics()
    {
        await using var db = TestDbContext.Create();
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 7);
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 8);
        var september = await SeedAsync(db, 7, Payout.StatusApprovedForSettlement, new DateOnly(2026, 9, 1), 1_000_000m);
        await SeedAsync(db, 8, Payout.StatusApprovedForSettlement, new DateOnly(2026, 7, 1), 1_000_000m);
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var overlapping = await handler.Handle(
            new GetPayoutSettlementsQuery(PeriodFrom: new DateOnly(2026, 9, 30), PeriodTo: new DateOnly(2026, 10, 5)),
            CancellationToken.None);
        var outside = await handler.Handle(
            new GetPayoutSettlementsQuery(PeriodFrom: new DateOnly(2026, 10, 1), PeriodTo: new DateOnly(2026, 10, 5)),
            CancellationToken.None);

        overlapping.Value.Items.Select(item => item.PayoutId).Should().Equal(september.Id.ToString());
        outside.Value.Items.Should().BeEmpty();
        outside.Value.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Handle_Paging_AppliesAfterFilteringWithStableOrderAndFullTotals()
    {
        await using var db = TestDbContext.Create();
        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            await PayoutTestFixture.SeedApprovedOperatorAsync(db, 20 + index);
            var payout = await SeedAsync(db, 20 + index, Payout.StatusApprovedForSettlement, new DateOnly(2026, 9, 1), 1_000_000m);
            ids.Add(payout.Id.ToString());
        }

        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var page2 = await handler.Handle(new GetPayoutSettlementsQuery(PageNumber: 2, PageSize: 2), CancellationToken.None);

        page2.Value.TotalCount.Should().Be(5);
        page2.Value.TotalPages.Should().Be(3);
        page2.Value.Items.Should().HaveCount(2);
        page2.Value.Items.Select(item => item.PayoutId).Should().BeEquivalentTo(
            ids.AsEnumerable().Reverse().Skip(2).Take(2),
            options => options.WithStrictOrdering(),
            "equal periods fall back to operator id then payout id, newest first");
    }

    [Fact]
    public async Task Handle_ResponseCarriesNoBeneficiaryReviewerOrGatewayData()
    {
        await using var db = TestDbContext.Create();
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 7);
        await SeedAsync(db, 7, Payout.StatusSettled, new DateOnly(2026, 9, 1), 4_000_000m);
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        var result = await handler.Handle(new GetPayoutSettlementsQuery(Status: Payout.StatusSettled), CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Value).ToLowerInvariant();
        json.Should().NotContain("bank").And.NotContain("account").And.NotContain("gateway")
            .And.NotContain("reviewed").And.NotContain("protected").And.NotContain("payos")
            .And.NotContain("1029384756");
    }

    [Fact]
    public async Task Handle_Query_DoesNotMutateAnyPayout()
    {
        await using var db = TestDbContext.Create();
        await PayoutTestFixture.SeedApprovedOperatorAsync(db, 7);
        var payout = await SeedAsync(db, 7, Payout.StatusApprovedForSettlement, new DateOnly(2026, 9, 1), 4_000_000m);
        var handler = new GetPayoutSettlementsQueryHandler(db, Admin());

        await handler.Handle(new GetPayoutSettlementsQuery(), CancellationToken.None);

        db.ChangeTracker.HasChanges().Should().BeFalse();
        (await db.Payouts.FindAsync(payout.Id))!.Status.Should().Be(Payout.StatusApprovedForSettlement);
        db.Payouts.Should().NotContain(item => item.SettlementAuthorizedBy != null);
    }

    /// <summary>Seeds a payout and moves it to <paramref name="status"/> through the domain methods.</summary>
    internal static async Task<Payout> SeedAsync(
        TestDbContext db, long operatorUserId, string status, DateOnly month, decimal paymentsReceived)
    {
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            db, operatorUserId, month, paymentsReceived, commissionAmount: paymentsReceived / 10m);
        payout.Approve(5, Now.AddDays(-1));
        if (status != Payout.StatusApprovedForSettlement)
        {
            payout.AuthorizeSettlement(9, Now, "970436");
            switch (status)
            {
                case Payout.StatusSettlementProcessing:
                    break;
                case Payout.StatusSettled:
                    payout.RecordGatewayAttempt(Now.AddMinutes(1));
                    payout.SetGatewayReference("payos-secret-ref");
                    payout.MarkSettled();
                    break;
                case Payout.StatusSettlementFailed:
                    payout.RecordGatewayAttempt(Now.AddMinutes(1));
                    payout.MarkSettlementFailed("gateway_rejected");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        await db.SaveChangesAsync();
        return payout;
    }

    internal static CurrentUser Admin() => new(1, "Administrator");

    internal sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;
}
