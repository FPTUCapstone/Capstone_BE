using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Weather;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Weather;

public sealed class ReviewTourWeatherReadinessCommandHandlerTests
{
    [Fact]
    public async Task Handle_OwnerCanReviewWeatherDataForPublishedTour()
    {
        await using var db = TestDbContext.Create();
        var now = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        var operatorUser = await SeedOperatorAsync(db, 7);
        var (tourId, stopId, departureId) = await SeedPublishedTourAsync(db, operatorUser.Id, now);
        var handler = new ReviewTourWeatherReadinessCommandHandler(db,
            new FakeCurrentUserService { UserId = operatorUser.Id },
            new FakeDateTimeProvider { UtcNow = now });

        var result = await handler.Handle(new ReviewTourWeatherReadinessCommand(tourId,
            [new WeatherStopReview(stopId, true)],
            [new WeatherDepartureReview(departureId, "  City Hall  ", 16.0471m, 108.2068m)]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var tour = await db.Tours.Include(candidate => candidate.ItineraryItems)
            .Include(candidate => candidate.Schedules).SingleAsync();
        tour.Status.Should().Be(TourStatus.Published);
        tour.ItineraryItems.Single().IsRequiredForWeather.Should().BeTrue();
        tour.Schedules.Single().MeetingPoint.Should().Be("City Hall");
        tour.Schedules.Single().MeetingLatitude.Should().Be(16.0471m);
    }

    [Fact]
    public async Task Handle_IncompleteReviewLeavesAllWeatherDataUnchanged()
    {
        await using var db = TestDbContext.Create();
        var now = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        var operatorUser = await SeedOperatorAsync(db, 7);
        var (tourId, stopId, _) = await SeedPublishedTourAsync(db, operatorUser.Id, now);
        var handler = new ReviewTourWeatherReadinessCommandHandler(db,
            new FakeCurrentUserService { UserId = operatorUser.Id },
            new FakeDateTimeProvider { UtcNow = now });

        var result = await handler.Handle(new ReviewTourWeatherReadinessCommand(tourId,
            [new WeatherStopReview(stopId, true)], []), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        (await db.TourItineraryItems.SingleAsync()).IsRequiredForWeather.Should().BeNull();
        (await db.TourSchedules.SingleAsync()).MeetingLatitude.Should().BeNull();
    }

    private static async Task<(long TourId, long StopId, long DepartureId)> SeedPublishedTourAsync(
        TestDbContext db, long operatorId, DateTimeOffset now)
    {
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 88L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var command = new CreateTourPackageCommand("Da Nang tour", "A full day tour", null, 1_000_000m, 1,
            [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
            [new CreateTourItineraryItemRequest(1, 1, 88, new TimeOnly(9, 0), 60, null)],
            [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), null, 20)],
            Guid.NewGuid());
        var created = await new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = operatorId }, new FakeDateTimeProvider { UtcNow = now })
            .Handle(command, CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var tour = await db.Tours.Include(candidate => candidate.ItineraryItems)
            .Include(candidate => candidate.Schedules).SingleAsync();
        tour.SubmitForReview(now);
        tour.Approve(99, now);
        tour.Publish(99, now);
        await db.SaveChangesAsync();
        return (tour.Id, tour.ItineraryItems.Single().Id, tour.Schedules.Single().Id);
    }

    private static async Task<User> SeedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = "operator@test.local", FullName = "Operator",
            Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = "Tour Co",
            TaxCode = "Tax7", BusinessLicenseNo = "Lic7", ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        return user;
    }
}
