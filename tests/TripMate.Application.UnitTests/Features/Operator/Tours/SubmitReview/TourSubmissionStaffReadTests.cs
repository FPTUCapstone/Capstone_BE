using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.SubmitReview;

public sealed class TourSubmissionStaffReadTests
{
    [Theory]
    [InlineData(UserRole.Staff, AccountStatus.Locked)]
    [InlineData(UserRole.Administrator, AccountStatus.Active)]
    [InlineData(UserRole.Traveler, AccountStatus.Active)]
    public async Task Queue_RejectsNonActiveStaff(UserRole role, AccountStatus status)
    {
        await using var db = TestDbContext.Create();
        db.Users.Add(new User { Id = 42, Email = "staff@test.local", FullName = "Staff", Role = role, Status = status });
        await db.SaveChangesAsync();
        var handler = new GetTourReviewQueueQueryHandler(db, new FakeCurrentUserService { UserId = 42, Role = role.ToString() });
        var result = await handler.Handle(new GetTourReviewQueueQuery(), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("operator_tour.admin_access_required");
    }
}