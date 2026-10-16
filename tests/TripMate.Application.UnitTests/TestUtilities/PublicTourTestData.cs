using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>Builds tours in a chosen lifecycle state for the public tour read use cases (UC-24/UC-26).</summary>
internal static class PublicTourTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);

    public static async Task<User> SeedOperatorAsync(
        TestDbContext db,
        long id,
        OperatorApprovalStatus approval = OperatorApprovalStatus.Approved,
        AccountStatus accountStatus = AccountStatus.Active,
        string company = "Hoi An Tours",
        string? phone = "0900000000",
        string? address = "1 Private Street")
    {
        var user = new User
        {
            Id = id,
            Email = $"operator-{id}@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = accountStatus,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id,
            User = user,
            CompanyName = company,
            TaxCode = $"TAX-{id}",
            BusinessLicenseNo = $"LIC-{id}",
            ContactPhone = phone,
            ContactAddress = address,
            ApprovalStatus = approval,
        });
        await db.SaveChangesAsync();
        return user;
    }

    public static Tour NewTour(
        long operatorId,
        string title = "Hoi An food tour",
        IReadOnlyList<(DateTimeOffset Start, int Total, decimal? Adult)>? departures = null,
        IReadOnlyList<(int Day, int Sequence, long PoiId)>? stops = null)
    {
        var scheduleSpecs = departures ?? [(Now.AddDays(10), 20, 1_500_000m)];
        var stopSpecs = stops ?? [(1, 1, 100L), (1, 2, 101L), (2, 1, 100L)];
        return Tour.CreateDraft(
            operatorId,
            categoryId: null,
            title,
            "A relaxed two day food tour.",
            1_500_000m,
            durationDays: 2,
            destinationIds: [TourAuthoringTestData.SecondDestinationId, TourAuthoringTestData.DestinationId],
            destinationSummary: "Hoi An, Da Nang",
            TourAuthoringTestData.NewPolicy(),
            stopSpecs.Select(stop => TourItineraryItem.Create(stop.Day, stop.Sequence, stop.PoiId,
                new TimeOnly(8, 30), 90, "Local guide")),
            scheduleSpecs.Select(spec => TourSchedule.Create(spec.Start, spec.Start.AddDays(2), "Hotel lobby",
                spec.Total, Now.AddDays(-30), minimumCapacity: 1, registrationDeadlineAtUtc: null,
                adultPrice: spec.Adult, childPrice: 900_000m, infantPrice: 0m)),
            Now.AddDays(-30),
            "Breakfast and guide",
            "Personal spending");
    }

    /// <summary>Adds a tour and moves it through the real lifecycle methods to <paramref name="status"/>.</summary>
    public static async Task<Tour> AddTourAsync(
        TestDbContext db,
        long operatorId,
        TourStatus status,
        string title = "Hoi An food tour",
        IReadOnlyList<(DateTimeOffset Start, int Total, decimal? Adult)>? departures = null,
        IReadOnlyList<(int Day, int Sequence, long PoiId)>? stops = null)
    {
        var tour = NewTour(operatorId, title, departures, stops);
        var when = Now.AddDays(-2);
        switch (status)
        {
            case TourStatus.Draft:
                break;
            case TourStatus.Pending:
                tour.SubmitForReview(when);
                break;
            case TourStatus.Rejected:
                tour.SubmitForReview(when);
                tour.Reject(2, "Missing details", when);
                break;
            case TourStatus.Approved:
                tour.SubmitForReview(when);
                tour.Approve(2, when);
                break;
            case TourStatus.Published:
                tour.SubmitForReview(when);
                tour.Approve(2, when);
                tour.Publish(2, when);
                break;
            case TourStatus.Inactive:
                tour.SubmitForReview(when);
                tour.Approve(2, when);
                tour.Publish(2, when);
                tour.Unpublish(when);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        return tour;
    }

    /// <summary>Legacy shape: Approved with a publication date and no Published status.</summary>
    public static async Task<Tour> AddApprovedWithPublicationDateAsync(
        TestDbContext db, long operatorId, DateTimeOffset publishedAtUtc)
    {
        var tour = await AddTourAsync(db, operatorId, TourStatus.Approved);
        typeof(Tour).GetProperty(nameof(Tour.PublishedAtUtc))!.SetValue(tour, publishedAtUtc);
        await db.SaveChangesAsync();
        return tour;
    }

    public static async Task<PointOfInterest> AddPoiAsync(
        TestDbContext db,
        long id,
        string name,
        string? address,
        PointOfInterestStatus status = PointOfInterestStatus.Active)
    {
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poi, name);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Address))!.SetValue(poi, address);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poi, 15.88m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poi, 108.33m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, status);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        return poi;
    }
}
