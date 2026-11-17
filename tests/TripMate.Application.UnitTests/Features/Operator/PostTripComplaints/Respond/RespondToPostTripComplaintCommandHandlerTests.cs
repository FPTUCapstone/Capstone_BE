using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.PostTripComplaints.Common;
using TripMate.Application.Features.Operator.PostTripComplaints.Respond;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.PostTripComplaints.Respond;

public sealed class RespondToPostTripComplaintCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProposePartialRefund_WithReason_PersistsOfferAndTravelerDeadline()
    {
        await using var db = TestDbContext.Create();
        PostTripComplaint complaint = await SeedOpenComplaintAsync(db);
        var handler = CreateHandler(db);

        var result = await handler.Handle(new RespondToPostTripComplaintCommand(complaint.Id, Guid.NewGuid(),
            PostTripComplaintResponseAction.ProposePartialRefund, 40_000m, "The itinerary item was unavailable.", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(PostTripComplaintStatus.AwaitingTravelerResponse));
        result.Value.ProposedRefundAmount.Should().Be(40_000m);
        result.Value.TravelerResponseDeadlineUtc.Should().Be(Now.AddDays(3));
        (await db.PostTripComplaints.FindAsync(complaint.Id))!.PartialRefundProposalReason
            .Should().Be("The itinerary item was unavailable.");
        (await db.Notifications.SingleAsync()).UserId.Should().Be(7);
    }

    [Fact]
    public async Task ProposePartialRefund_WithoutReason_ReturnsValidationErrorAndKeepsComplaintOpen()
    {
        await using var db = TestDbContext.Create();
        PostTripComplaint complaint = await SeedOpenComplaintAsync(db);
        var handler = CreateHandler(db);

        var result = await handler.Handle(new RespondToPostTripComplaintCommand(complaint.Id, Guid.NewGuid(),
            PostTripComplaintResponseAction.ProposePartialRefund, 40_000m, " ", null), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorPostTripComplaintErrorCodes.ProposalReasonRequired);
        (await db.PostTripComplaints.FindAsync(complaint.Id))!.Status
            .Should().Be(PostTripComplaintStatus.AwaitingOperatorResponse);
        (await db.PostTripComplaintResponseOperations.AnyAsync()).Should().BeFalse();
    }

    private static RespondToPostTripComplaintCommandHandler CreateHandler(TestDbContext db) =>
        new(db, new FakeCurrentUserService { UserId = 8, Role = nameof(UserRole.TourOperator) },
            new FakeDateTimeProvider { UtcNow = Now });

    private static async Task<PostTripComplaint> SeedOpenComplaintAsync(TestDbContext db)
    {
        var operatorUser = new User { Id = 8, Role = UserRole.TourOperator, Status = AccountStatus.Active, FullName = "Operator" };
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 21L);
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "Da Nang escape");
        Set(tour, nameof(Tour.BasePrice), 100_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), 31L);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), Now.AddDays(-5));
        Set(schedule, nameof(TourSchedule.EndAtUtc), Now.AddDays(-4));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        Booking booking = Booking.Restore(41, 7, schedule.Id, 1, 100_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, Booking.DefaultCancellationPolicySnapshot);
        PostTripComplaint complaint = PostTripComplaint.Create(booking.Id, 7,
            PostTripComplaintReasonCategory.ServiceQuality, "The guide arrived late.", Now.AddDays(-1),
            Now.AddDays(2), 80_000m);
        Set(complaint, nameof(PostTripComplaint.Id), 61L);
        db.Users.Add(operatorUser);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        db.PostTripComplaints.Add(complaint);
        await db.SaveChangesAsync();
        return complaint;
    }

    private static void Set<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}
