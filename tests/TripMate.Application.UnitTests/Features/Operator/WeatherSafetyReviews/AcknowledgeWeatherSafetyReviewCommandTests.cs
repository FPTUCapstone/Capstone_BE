using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.WeatherSafetyReviews;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.WeatherSafetyReviews;

public sealed class AcknowledgeWeatherSafetyReviewCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private sealed class NullNotifier : IWeatherSafetyReviewNotifier
    {
        public Task NotifyChangedAsync(
            long operatorUserId,
            WeatherSafetyReviewChanged change,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Handle_WhenOperatorIsLocked_RefusesAndLeavesTheReviewPending()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = new User
        {
            Role = UserRole.TourOperator,
            Status = AccountStatus.Locked,
            Email = "operator@example.test",
            FullName = "UC70 Operator",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(operatorUser, 2L);
        db.Users.Add(operatorUser);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 2,
            User = operatorUser,
            CompanyName = "UC70 Tours",
            TaxCode = "UC70-TAX",
            BusinessLicenseNo = "UC70-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        var schedule = TourSchedule.Create(Now.AddDays(1), Now.AddDays(1).AddHours(8), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 5L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, 1L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        var review = WeatherSafetyReview.CreateHold(5, "Fixture", "evt-1", Now, Now.AddMinutes(30));
        db.WeatherSafetyReviews.Add(review);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };

        var result = await new AcknowledgeWeatherSafetyReviewCommandHandler(db, currentUser, clock, new NullNotifier())
            .Handle(new AcknowledgeWeatherSafetyReviewCommand(review.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(WeatherSafetyReviewErrorCodes.Forbidden);
        (await db.WeatherSafetyReviews.SingleAsync()).Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
    }

    [Theory]
    [InlineData(WeatherSafetyReview.InstructionsMaxLength, true)]
    [InlineData(WeatherSafetyReview.InstructionsMaxLength + 1, false)]
    public void Validator_LimitsInstructionsToTheStoredLength(int length, bool expectedValid)
    {
        var result = new AcknowledgeWeatherSafetyReviewCommandValidator()
            .Validate(new AcknowledgeWeatherSafetyReviewCommand(1, new string('x', length)));

        result.IsValid.Should().Be(expectedValid);
    }
}
