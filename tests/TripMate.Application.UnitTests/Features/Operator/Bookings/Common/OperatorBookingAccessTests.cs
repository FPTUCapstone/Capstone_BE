using FluentAssertions;

using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Bookings.Common;

public sealed class OperatorBookingAccessTests
{
    [Fact]
    public async Task ResolveAsync_ApprovedActiveOperator_ReturnsOperatorId()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);

        var result = await OperatorBookingAccess.ResolveOperatorIdAsync(db, OperatorUser(operatorUser.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(7);
    }

    [Fact]
    public async Task ResolveAsync_Unauthenticated_IsDenied()
    {
        await using var db = TestDbContext.Create();

        var result = await OperatorBookingAccess.ResolveOperatorIdAsync(db,
            new FakeCurrentUserService { UserId = null, Role = nameof(UserRole.TourOperator) },
            CancellationToken.None);

        AssertDenied(result);
    }

    [Theory]
    [InlineData(nameof(UserRole.Traveler))]
    [InlineData(nameof(UserRole.Staff))]
    [InlineData(nameof(UserRole.Administrator))]
    [InlineData(null)]
    public async Task ResolveAsync_NonOperatorRole_IsDenied(string? role)
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);

        var result = await OperatorBookingAccess.ResolveOperatorIdAsync(db,
            new FakeCurrentUserService { UserId = operatorUser.Id, Role = role }, CancellationToken.None);

        AssertDenied(result);
    }

    [Theory]
    [InlineData(OperatorApprovalStatus.PendingApproval)]
    [InlineData(OperatorApprovalStatus.Rejected)]
    public async Task ResolveAsync_OperatorNotApproved_IsDenied(OperatorApprovalStatus approval)
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        Set(db.OperatorProfiles.Single(p => p.UserId == 7), nameof(OperatorProfile.ApprovalStatus), approval);
        await db.SaveChangesAsync();

        var result = await OperatorBookingAccess.ResolveOperatorIdAsync(db, OperatorUser(operatorUser.Id),
            CancellationToken.None);

        AssertDenied(result);
    }

    [Fact]
    public async Task ResolveAsync_OperatorAccountNotActive_IsDenied()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        operatorUser.Status = AccountStatus.Locked;
        await db.SaveChangesAsync();

        var result = await OperatorBookingAccess.ResolveOperatorIdAsync(db, OperatorUser(operatorUser.Id),
            CancellationToken.None);

        AssertDenied(result);
    }

    [Fact]
    public async Task ResolveAsync_OperatorWithoutProfile_IsDenied()
    {
        await using var db = TestDbContext.Create();
        db.Users.Add(new User
        {
            Id = 8,
            Email = "noprofile@test.local",
            FullName = "No Profile",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        });
        await db.SaveChangesAsync();

        var result = await OperatorBookingAccess.ResolveOperatorIdAsync(db, OperatorUser(8), CancellationToken.None);

        AssertDenied(result);
    }

    [Fact]
    public void ErrorCodes_AreStable()
    {
        OperatorBookingErrorCodes.InvalidFilter.Should().Be("operator_booking.invalid_filter");
        OperatorBookingErrorCodes.InvalidDateRange.Should().Be("operator_booking.invalid_date_range");
        OperatorBookingErrorCodes.Forbidden.Should().Be("operator_booking.forbidden");
        OperatorBookingErrorCodes.NotFound.Should().Be("operator_booking.not_found");
    }

    private static FakeCurrentUserService OperatorUser(long id) =>
        new() { UserId = id, Role = nameof(UserRole.TourOperator) };

    private static void AssertDenied(TripMate.Application.Common.Models.Result<long> result)
    {
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }
}