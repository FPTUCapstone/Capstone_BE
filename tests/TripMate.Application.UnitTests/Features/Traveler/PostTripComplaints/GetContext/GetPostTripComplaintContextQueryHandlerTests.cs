using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Application.Features.Traveler.PostTripComplaints.GetContext;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.PostTripComplaints.GetContext;

public sealed class GetPostTripComplaintContextQueryHandlerTests
{
    [Fact]
    public async Task Handle_EligibleOwnedBooking_ReturnsSafeSubmitContext()
    {
        await using var db = TestDbContext.Create();
        DateTimeOffset endAt = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        Booking booking = await SeedBookingAsync(db, endAt);
        var handler = new GetPostTripComplaintContextQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = endAt.AddDays(1) });

        var result = await handler.Handle(new GetPostTripComplaintContextQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            BookingId = booking.Id,
            TourTitle = "Da Nang escape",
            EndAtUtc = endAt,
            SubmissionDeadlineUtc = endAt.AddDays(3),
            CanSubmit = true,
            IneligibilityReason = (string?)null,
            Complaint = (ExistingPostTripComplaintResponse?)null,
        });
    }

    [Fact]
    public async Task Handle_ExistingComplaint_ReturnsOnlySafeEvidenceMetadataAndBlocksSecondSubmission()
    {
        await using var db = TestDbContext.Create();
        DateTimeOffset endAt = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        Booking booking = await SeedBookingAsync(db, endAt);
        PostTripComplaint complaint = PostTripComplaint.Create(booking.Id, 7,
            PostTripComplaintReasonCategory.ServiceQuality, "Guide arrived late.", endAt,
            endAt.AddDays(7), 80_000m);
        Set(complaint, "Id", 77L);
        db.PostTripComplaints.Add(complaint);
        db.ComplaintEvidence.Add(ComplaintEvidence.Create(complaint.Id, "receipt.pdf", "application/pdf",
            512L, "private/complaints/77/receipt.pdf", "hash", endAt));
        await db.SaveChangesAsync();

        var handler = new GetPostTripComplaintContextQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = endAt.AddDays(1) });

        var result = await handler.Handle(new GetPostTripComplaintContextQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CanSubmit.Should().BeFalse();
        result.Value.IneligibilityReason.Should().Be(PostTripComplaintErrorCodes.Duplicate);
        result.Value.Complaint!.Evidence.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FileName = "receipt.pdf", ContentType = "application/pdf", SizeBytes = 512L,
        });
    }

    private static async Task<Booking> SeedBookingAsync(TestDbContext db, DateTimeOffset endAt)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 21L);
        Set(tour, nameof(Tour.OperatorUserId), 8L);
        Set(tour, nameof(Tour.Title), "Da Nang escape");
        Set(tour, nameof(Tour.BasePrice), 100m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), 31L);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), endAt.AddHours(-8));
        Set(schedule, nameof(TourSchedule.EndAtUtc), endAt);
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), 1);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        Booking booking = Booking.Restore(41, 7, schedule.Id, 1, 100_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, Booking.DefaultCancellationPolicySnapshot);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static void Set<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}
