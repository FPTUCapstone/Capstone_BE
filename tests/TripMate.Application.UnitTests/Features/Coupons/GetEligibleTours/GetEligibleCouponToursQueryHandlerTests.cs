using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Coupons.GetEligibleTours;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.GetEligibleTours;

public sealed class GetEligibleCouponToursQueryHandlerTests
{
    [Fact]
    public async Task Handle_UsesTheFirstDestinationInTheTourItinerary()
    {
        await using var db = TestDbContext.Create();
        var user = await SeedApprovedOperatorAsync(db);
        var tour = CreateTour(user.Id);
        var firstDestination = CreateDestination(10, "Z first destination");
        var secondDestination = CreateDestination(20, "A later destination");
        db.AddRange(tour, firstDestination, secondDestination);
        db.AddRange(
            CreateTourDestination(tour.Id, firstDestination.Id, 1),
            CreateTourDestination(tour.Id, secondDestination.Id, 2));
        await db.SaveChangesAsync();
        var handler = new GetEligibleCouponToursQueryHandler(
            db,
            new FakeCurrentUserService { UserId = user.Id });

        var result = await handler.Handle(new GetEligibleCouponToursQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Destination.Should().Be("Z first destination");
    }

    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db)
    {
        var user = new User
        {
            Id = 7,
            Email = "operator@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Company",
            TaxCode = "Tax",
            BusinessLicenseNo = "License",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static Tour CreateTour(long operatorId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 101L);
        Set(tour, nameof(Tour.OperatorUserId), operatorId);
        Set(tour, nameof(Tour.Title), "Coastal itinerary");
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.DurationDays), 2);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        return tour;
    }

    private static Destination CreateDestination(long id, string name)
    {
        var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
        Set(destination, nameof(Destination.Id), id);
        Set(destination, nameof(Destination.Name), name);
        return destination;
    }

    private static TourDestination CreateTourDestination(long tourId, long destinationId, int sequenceNo)
    {
        var mapping = (TourDestination)Activator.CreateInstance(typeof(TourDestination), nonPublic: true)!;
        Set(mapping, nameof(TourDestination.TourId), tourId);
        Set(mapping, nameof(TourDestination.DestinationId), destinationId);
        Set(mapping, nameof(TourDestination.SequenceNo), sequenceNo);
        return mapping;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}