using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Rerouting.Engine;

/// <summary>
/// Shared arrangement for the UC-15 engine tests: one trip (Tuesday 09:00-17:00 Asia/Ho_Chi_Minh, POIs open
/// 07:00-20:00 on Tuesday, every leg takes 10 minutes) with a Navigating session over saved itinerary items.
/// </summary>
internal sealed class ReroutingTestKit : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset RequestStart = Now.AddHours(1);
    public const decimal CenterLatitude = 16.0471m;
    public const decimal CenterLongitude = 108.2068m;
    public const long TravelerId = 42;

    private readonly Dictionary<string, PoiCategory> _categories = [];
    private int _poiCounter;

    private ReroutingTestKit(TestDbContext db) => Db = db;

    public TestDbContext Db { get; }

    public RecordingRouting Routing { get; } = new();

    public SchedulingRequest Request { get; private set; } = null!;

    public Itinerary Itinerary { get; private set; } = null!;

    public ItineraryVersionService Service => new(Db, Routing);

    public static Task<ReroutingTestKit> CreateAsync() => Task.FromResult(new ReroutingTestKit(TestDbContext.Create()));

    /// <summary>Latitude offset that moves a POI roughly <paramref name="kilometers"/> km north of the centre.</summary>
    public static decimal NorthByKm(double kilometers) => CenterLatitude + (decimal)(kilometers / 111.0);

    public async Task<PointOfInterest> AddPoiAsync(
        string name,
        decimal? latitude = null,
        string category = "Culture",
        IndoorOutdoorType indoorOutdoor = IndoorOutdoorType.Outdoor,
        bool reviewed = false,
        int visitMinutes = 60,
        bool planningReady = true,
        TimeOnly? opensAt = null,
        TimeOnly? closesAt = null)
    {
        if (!_categories.TryGetValue(category, out var poiCategory))
        {
            poiCategory = PoiCategory.Create(category, null);
            _categories[category] = poiCategory;
        }

        var poi = PointOfInterest.Create(
            poiCategory,
            name,
            latitude ?? CenterLatitude + _poiCounter++ * 0.0005m,
            CenterLongitude,
            1,
            Now,
            indoorOutdoor: indoorOutdoor,
            averageVisitDurationMinutes: visitMinutes);
        if (planningReady)
        {
            poi.ConfigurePlanningMetadata(60_000m, "https://example.com/poi", Now);
            poi.AddOpeningHour(PoiOpeningHour.Create(
                2, opensAt ?? new TimeOnly(7, 0), closesAt ?? new TimeOnly(20, 0), false));
        }

        if (reviewed)
        {
            poi.ReviewIndoorOutdoor(indoorOutdoor, Now);
        }

        Db.PointsOfInterest.Add(poi);
        await Db.SaveChangesAsync();
        return poi;
    }

    public async Task<SchedulingRequest> AddRequestAsync(
        long[]? mandatoryPoiIds = null,
        decimal? budgetVnd = 800_000m,
        int availableMinutes = 480)
    {
        var request = SchedulingRequest.Create(
            TravelerId,
            Guid.NewGuid(),
            new string('a', SchedulingRequest.RequestHashLength),
            RequestStart,
            "Asia/Ho_Chi_Minh",
            CenterLatitude,
            CenterLongitude,
            CenterLatitude,
            CenterLongitude,
            null,
            true,
            availableMinutes,
            TransportMode.Motorbike,
            10m,
            budgetVnd,
            System.Text.Json.JsonSerializer.Serialize(mandatoryPoiIds ?? []),
            RestPreference.None,
            Now);
        var owner = Guid.NewGuid();
        request.ClaimGeneration(owner, Now.AddMinutes(1), Now);
        request.CompleteGeneration(owner, Now);
        Db.SchedulingRequests.Add(request);
        await Db.SaveChangesAsync();
        Request = request;
        return request;
    }

    /// <summary>Saves an Active itinerary (one visit per POI, 60 min stay + 10 min leg) and starts a Navigating session on it.</summary>
    public async Task<TripSession> StartSessionAsync(params PointOfInterest[] pois)
    {
        var mandatoryIds = System.Text.Json.JsonSerializer.Deserialize<long[]>(Request.MandatoryPoiIdsJson) ?? [];
        Itinerary = Itinerary.CreateCspGenerated(Request, "Generated trip", RequestStart, RequestStart.AddHours(8), 1);
        for (var index = 0; index < pois.Length; index++)
        {
            var arrival = RequestStart.AddMinutes(10 + index * 70);
            Itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1,
                pois[index].Id,
                arrival,
                arrival.AddMinutes(60),
                mandatoryIds.Contains(pois[index].Id),
                60_000m,
                "Suggested nearby location",
                travelDurationToNextMinutes: index == pois.Length - 1 ? null : 10,
                travelDistanceToNextMeters: index == pois.Length - 1 ? null : 4000));
        }

        Itinerary.Accept(Now);
        Db.Itineraries.Add(Itinerary);
        await Db.SaveChangesAsync();

        var snapshot = Itinerary.Items.OrderBy(item => item.SequenceNo)
            .Select(item =>
            {
                var poi = pois.Single(candidate => candidate.Id == item.PointOfInterestId);
                return TripSessionItem.Snapshot(
                    item.Id,
                    item.SequenceNo,
                    poi.Id,
                    poi.Name,
                    poi.Latitude,
                    poi.Longitude,
                    item.PlannedArrivalUtc!.Value,
                    item.PlannedDepartureUtc!.Value,
                    item.IsMandatory);
            })
            .ToArray();
        var session = TripSession.Start(
            Itinerary.Id,
            Itinerary.Id,
            TravelerId,
            Guid.NewGuid(),
            RequestStart,
            RequestStart.AddHours(12),
            snapshot);
        Db.TripSessions.Add(session);
        await Db.SaveChangesAsync();
        return session;
    }

    public async Task<Incident> AddIncidentAsync(TripSession session, string incidentType, long affectedItemId)
    {
        var incident = Incident.CreateTripAlert(
            session, incidentType, $"{incidentType}:{affectedItemId}:{Guid.NewGuid():N}", Now, affectedItemId);
        Db.Incidents.Add(incident);
        await Db.SaveChangesAsync();
        return incident;
    }

    public long ItemIdOf(TripSession session, PointOfInterest poi) =>
        session.Items.Single(item => item.PoiId == poi.Id).ItineraryItemId;

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    internal sealed class RecordingRouting : IRouteDurationProvider
    {
        public List<IReadOnlyList<RoutePoint>> Calls { get; } = [];

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            Calls.Add(points.ToArray());
            var durations = new int[points.Count, points.Count];
            var meters = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 10;
                    meters[row, column] = row == column ? 0 : 4000;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations, unreachable: null, meters: meters));
        }
    }
}