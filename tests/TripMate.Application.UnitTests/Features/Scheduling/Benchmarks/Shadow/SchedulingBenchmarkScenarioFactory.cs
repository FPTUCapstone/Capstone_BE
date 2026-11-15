using System.Text.Json;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Golden;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public static class SchedulingBenchmarkScenarioFactory
{
    internal const double EarthRadiusKm = 6371.0;
    private const double WindingFactor = 1.4;
    private const double AverageSpeedKph = 30.0;
    private static readonly string[] TargetCorpusScenarioIds =
    [
        "grid-c10-m0-none-s1",
        "grid-c10-m1-none-s1",
        "grid-c10-m3-auto-s1",
        "grid-c10-m1-frequent-s1",
        "grid-c20-m0-none-s1",
        "grid-c20-m1-none-s1",
        "grid-c20-m3-auto-s1",
        "grid-c20-m6-auto-s1",
        "grid-c20-m1-frequent-s1",
        "grid-c20-m3-frequent-s1",
        "grid-c40-m0-none-s1",
        "grid-c40-m1-none-s1",
        "grid-c40-m3-none-s1",
        "grid-c40-m6-none-s1",
        "grid-c40-m1-auto-s1",
        "grid-c40-m6-frequent-s1",
        "named-zigzag",
        "named-two-opt",
        "incumbent-valid-c20-m2-none-s10",
        "budget-limited-optional-s23"
    ];

    public static CityBenchmarkScenarioData CreateDaNangBaselineData()
    {
        var openingHours = new[] { new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0)) };
        var hotel = new RoutePoint(16.0610m, 108.2250m);

        return new CityBenchmarkScenarioData(
            Id: "danang-real-1day",
            Source: "Legacy benchmark fixture derived from database/seeds/20260914_seed_danang_planning_pois.sql, 20261015_seed_danang_city_real_pois.sql, and benchmark-only metadata",
            ExpectedFeasible: true,
            StartAtUtc: new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
            TimeZoneId: "Asia/Ho_Chi_Minh",
            Start: hotel,
            End: hotel,
            AvailableMinutes: 480,
            TransportMode: TransportMode.Motorbike,
            RestPreference: RestPreference.None,
            BudgetVnd: 1_000_000m,
            MandatoryPoiIds: [1L],
            Pois:
            [
                Poi(1, "Bảo tàng Điêu khắc Chăm", 16.040393m, 108.223058m, PoiCategoryNames.Museum, 90, 60_000m, IndoorOutdoorType.Indoor, true, 8m, 8m, ["culture", "museum", "history"]),
                Poi(2, "Bảo tàng Đà Nẵng", 16.071218m, 108.225226m, PoiCategoryNames.Museum, 90, 50_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["culture", "museum", "history"]),
                Poi(3, "Bảo tàng Mỹ thuật Đà Nẵng", 16.073538m, 108.219823m, PoiCategoryNames.Museum, 75, 20_000m, IndoorOutdoorType.Indoor, true, 8m, 9m, ["culture", "photography", "art"]),
                Poi(4, "Bảo tàng Quân khu 5", 16.032273m, 108.216323m, PoiCategoryNames.Museum, 90, 60_000m, IndoorOutdoorType.Indoor, true, 6m, 6m, ["history", "culture"]),
                Poi(5, "Cầu Rồng", 16.061158m, 108.230008m, PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false, 9m, 10m, ["architecture", "photography", "sightseeing"]),
                Poi(6, "Cầu Sông Hàn", 16.072159m, 108.226790m, PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false, 8m, 9m, ["architecture", "photography"]),
                Poi(7, "Cầu Tình Yêu", 16.062936m, 108.229821m, PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false, 8m, 9m, ["photography", "relaxation"]),
                Poi(8, "Công viên APEC", 16.058101m, 108.223250m, PoiCategoryNames.Nature, 45, 0m, IndoorOutdoorType.Outdoor, false, 8m, 9m, ["architecture", "relaxation", "photography"]),
                Poi(9, "Bãi biển Mỹ Khê", 16.069967m, 108.246066m, PoiCategoryNames.Beach, 120, 0m, IndoorOutdoorType.Outdoor, false, 9m, 9m, ["beach", "relaxation", "nature", "outdoor"]),
                Poi(10, "Chợ Cồn", 16.068420m, 108.214113m, PoiCategoryNames.Culture, 60, 0m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["food", "coffee", "culture", "shopping"]),
                Poi(11, "Chùa Linh Ứng Bãi Bụt", 16.099685m, 108.277042m, PoiCategoryNames.Culture, 60, 0m, IndoorOutdoorType.Mixed, true, 10m, 10m, ["heritage", "culture", "nature", "photography"]),
                Poi(12, "Ngũ Hành Sơn - Thủy Sơn", 16.003759m, 108.263876m, PoiCategoryNames.NaturalAttraction, 120, 40_000m, IndoorOutdoorType.Outdoor, false, 9m, 9m, ["nature", "adventure", "outdoor", "hiking"]),
                Poi(13, "Động Âm Phủ", 16.004488m, 108.264164m, PoiCategoryNames.NaturalAttraction, 45, 20_000m, IndoorOutdoorType.Mixed, false, 7m, 6m, ["nature", "adventure"]),
                Poi(14, "Làng đá mỹ nghệ Non Nước", 16.002829m, 108.261263m, PoiCategoryNames.Culture, 45, 0m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["heritage", "culture", "shopping"]),
                Poi(15, "Nhà hàng Madame Lân", 16.082500m, 108.223000m, PoiCategoryNames.FoodAndDrink, 60, 150_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["food", "cuisine", "coffee"]),
                Poi(16, "Cộng Cà Phê Bạch Đằng", 16.068000m, 108.225000m, PoiCategoryNames.FoodAndDrink, 45, 50_000m, IndoorOutdoorType.Indoor, true, 7m, 8m, ["coffee", "food", "relaxation"])
            ],
            Personas:
            [
                new("CultureEnthusiast", "Culture Enthusiast", TravelerPace.Moderate, RiskToleranceLevel.Medium, ["culture", "museum", "history", "heritage", "art"], [PoiCategoryNames.Culture, PoiCategoryNames.Museum], "Ưu tiên di tích, bảo tàng, lịch sử; nhịp độ vừa phải (tối đa 5 điểm/ngày)."),
                new("FoodLoverRelaxed", "Food Lover (Relaxed)", TravelerPace.Relaxed, RiskToleranceLevel.Low, ["food", "coffee", "cuisine", "culinary"], [PoiCategoryNames.FoodAndDrink], "Ưu tiên trải nghiệm ẩm thực, nghỉ ngơi; nhịp độ thong thả (tối đa 3 điểm/ngày)."),
                new("ActiveExplorerFast", "Active Explorer (Fast)", TravelerPace.Fast, RiskToleranceLevel.High, ["nature", "adventure", "outdoor", "hiking", "natural attraction"], [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction], "Ưu tiên thiên nhiên ngoài trời, tốc độ nhanh (tối đa 8 điểm/ngày)."),
                new("ScenicPhotography", "Scenic & Photography", TravelerPace.Moderate, RiskToleranceLevel.Low, ["photography", "scenic", "beach", "sightseeing", "nature"], [PoiCategoryNames.Culture, PoiCategoryNames.Nature, PoiCategoryNames.Beach], "Ưu tiên danh lam thắng cảnh, điểm chụp ảnh đẹp, nhịp độ vừa phải."),
                new("DefaultGeneralist", "Default Generalist (Cold Start)", TravelerPace.Moderate, RiskToleranceLevel.Medium, [], [], "Khách vãng lai chưa có hồ sơ; áp dụng cấu hình mặc định của hệ thống.")
            ]);

        BenchmarkPoiData Poi(
            long id,
            string name,
            decimal latitude,
            decimal longitude,
            string category,
            int duration,
            decimal cost,
            IndoorOutdoorType indoorOutdoor,
            bool hasShelter,
            decimal scenic,
            decimal photo,
            IReadOnlyList<string> tags) =>
            new(id, name, new RoutePoint(latitude, longitude), category, duration, cost, indoorOutdoor, hasShelter, scenic, photo, tags, openingHours);
    }

    public static CityBenchmarkScenarioData CreateDaNangV3bData()
    {
        var baseline = CreateDaNangBaselineData();
        var tuesday = (byte)2;
        BenchmarkPoiData[] natureSeedPois =
        [
            new(17, "Than Tai Hot Spring Park", new RoutePoint(15.987264m, 107.986255m),
                PoiCategoryNames.NaturalAttraction, 240, 490_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["nature"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(8, 30), new TimeOnly(17, 30))]),
            new(18, "Hoa Phu Thanh Tourist Area", new RoutePoint(15.973945m, 108.090233m),
                PoiCategoryNames.NaturalAttraction, 180, 150_000m, IndoorOutdoorType.Outdoor, true, 7m, 7m, ["nature", "outdoor"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(8, 0), new TimeOnly(16, 30))]),
            new(19, "Suoi Luong Ecotourism Area", new RoutePoint(16.135173m, 108.102806m),
                PoiCategoryNames.NaturalAttraction, 180, 80_000m, IndoorOutdoorType.Outdoor, true, 7m, 7m, ["nature", "outdoor"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(8, 0), new TimeOnly(17, 0))])
        ];

        return baseline with
        {
            Source = baseline.Source + "; nature extension from database/seeds/20260914_seed_danang_planning_pois.sql (tags derived only from canonical category and exposure)",
            Pois = [.. baseline.Pois, .. natureSeedPois]
        };
    }

    public static CityBenchmarkScenarioData CreateDaNangPersonalizationData()
    {
        var baseline = CreateDaNangV3bData();
        var tuesday = (byte)2;
        BenchmarkPoiData[] additionalPois =
        [
            new(20, "Sun World Ba Na Hills", new RoutePoint(15.997596m, 107.988215m),
                PoiCategoryNames.ThemePark, 300, 1_000_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["theme park", "relaxation"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(8, 0), new TimeOnly(22, 0))]),
            new(21, "Mikazuki 365 Water Park", new RoutePoint(16.125205m, 108.120618m),
                PoiCategoryNames.WaterPark, 240, 350_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["water park", "relaxation"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(9, 0), new TimeOnly(19, 0))]),
            new(22, "Dong Dinh Museum", new RoutePoint(16.112646m, 108.278505m),
                PoiCategoryNames.Museum, 90, 35_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["culture", "museum", "art"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(7, 0), new TimeOnly(17, 0))]),
            new(23, "Làng mộc Kim Bồng", new RoutePoint(15.865159m, 108.324237m),
                PoiCategoryNames.Culture, 60, 35_000m, IndoorOutdoorType.Outdoor, false, 7m, 7m, ["heritage", "culture"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(8, 0), new TimeOnly(17, 30))]),
            new(24, "Thánh địa Mỹ Sơn", new RoutePoint(15.768789m, 108.119316m),
                PoiCategoryNames.Culture, 150, 100_000m, IndoorOutdoorType.Outdoor, false, 7m, 7m, ["heritage", "history", "architecture"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(6, 0), new TimeOnly(18, 0))]),
            new(25, "VinWonders Nam Hội An", new RoutePoint(15.786494m, 108.408451m),
                PoiCategoryNames.ThemePark, 300, 650_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["theme park", "relaxation"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(9, 0), new TimeOnly(19, 0))]),
            new(26, "Mỳ Quảng Quê Xưa", new RoutePoint(16.046888m, 108.221160m),
                PoiCategoryNames.FoodAndDrink, 45, 57_500m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(6, 0), new TimeOnly(22, 30))]),
            new(27, "Bánh xèo Bà Dưỡng", new RoutePoint(16.058865m, 108.216435m),
                PoiCategoryNames.FoodAndDrink, 60, 50_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(9, 30), new TimeOnly(21, 30))]),
            new(28, "Mì Quảng Bà Mua", new RoutePoint(16.066019m, 108.219144m),
                PoiCategoryNames.FoodAndDrink, 45, 40_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
            new(29, "Bún chả cá 109", new RoutePoint(16.074418m, 108.220757m),
                PoiCategoryNames.FoodAndDrink, 45, 50_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(6, 30), new TimeOnly(22, 30))]),
            new(30, "Hải sản Mộc Quán", new RoutePoint(16.064016m, 108.241554m),
                PoiCategoryNames.FoodAndDrink, 75, 185_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(10, 30), new TimeOnly(22, 30))]),
            new(31, "Hải sản Bé Ni 2", new RoutePoint(16.066800m, 108.245089m),
                PoiCategoryNames.FoodAndDrink, 75, 0m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(0, 0), new TimeOnly(23, 59))]),
            new(32, "Hải sản Năm Đảnh", new RoutePoint(16.102249m, 108.253336m),
                PoiCategoryNames.FoodAndDrink, 75, 80_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(10, 0), new TimeOnly(22, 0))]),
            new(33, "Bánh Xèo 76", new RoutePoint(16.041647m, 108.240083m),
                PoiCategoryNames.FoodAndDrink, 45, 30_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(10, 0), new TimeOnly(21, 0))]),
            new(34, "Cơm gà Bà Buội", new RoutePoint(15.878479m, 108.330403m),
                PoiCategoryNames.FoodAndDrink, 45, 55_000m, IndoorOutdoorType.Indoor, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(10, 30), new TimeOnly(14, 0))]),
            new(35, "Morning Glory Signature", new RoutePoint(15.875959m, 108.326191m),
                PoiCategoryNames.FoodAndDrink, 75, 200_000m, IndoorOutdoorType.Mixed, true, 7m, 7m, ["food", "cuisine"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(10, 30), new TimeOnly(22, 30))]),
            new(36, "Đỉnh Bàn Cờ", new RoutePoint(16.118936m, 108.271983m),
                PoiCategoryNames.NaturalAttraction, 90, 0m, IndoorOutdoorType.Outdoor, false, 10m, 10m, ["photography", "scenic", "sightseeing"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(7, 30), new TimeOnly(17, 30))]),
            new(37, "Hải đăng Tiên Sa", new RoutePoint(16.139667m, 108.322083m),
                PoiCategoryNames.NaturalAttraction, 60, 0m, IndoorOutdoorType.Outdoor, false, 9m, 10m, ["photography", "scenic", "sightseeing"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(7, 0), new TimeOnly(17, 0))]),
            new(38, "Cây Đa Di Sản Sơn Trà", new RoutePoint(16.116682m, 108.292581m),
                PoiCategoryNames.NaturalAttraction, 60, 0m, IndoorOutdoorType.Outdoor, false, 9m, 8m, ["nature", "adventure", "outdoor", "hiking"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(7, 0), new TimeOnly(17, 0))]),
            new(39, "Cầu đi bộ Nguyễn Văn Trỗi", new RoutePoint(16.051390m, 108.231430m),
                PoiCategoryNames.Culture, 45, 0m, IndoorOutdoorType.Outdoor, false, 0m, 0m, ["photography", "scenic", "sightseeing"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(5, 0), new TimeOnly(23, 0))]),
            new(40, "Làng Bích họa Đà Nẵng", new RoutePoint(16.060710m, 108.220030m),
                PoiCategoryNames.Culture, 45, 0m, IndoorOutdoorType.Outdoor, false, 0m, 0m, ["photography", "scenic", "art"],
                [new GenerationOpeningHours(tuesday, new TimeOnly(7, 0), new TimeOnly(21, 0))])
        ];

        return baseline with
        {
            Source = baseline.Source + "; extension from database seeds plus reviewed Da Nang scenic POIs from the Da Nang Tourism Promotion Center and OpenStreetMap",
            Pois = [.. baseline.Pois, .. additionalPois]
        };
    }

    public static IReadOnlyList<GoldenScenario> CreateV3bScenarios(CityBenchmarkScenarioData cityData)
    {
        var corpus = GoldenScenarioFactory.CreateAllScenarios().ToDictionary(scenario => scenario.Id, StringComparer.Ordinal);
        var scenarios = TargetCorpusScenarioIds.Select(id => corpus[id]).ToList();
        scenarios.Add(CreateScenario(cityData));
        return scenarios;
    }

    public static GoldenScenario CreateScenario(CityBenchmarkScenarioData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Pois.Select(poi => poi.Id).Distinct().Count() != data.Pois.Count)
        {
            throw new ArgumentException("Benchmark POI IDs must be unique.", nameof(data));
        }

        var candidates = data.Pois.Select(poi => new GenerationCandidate(
            Id: poi.Id,
            Name: poi.Name,
            Location: poi.Location,
            VisitDurationMinutes: poi.VisitDurationMinutes,
            EstimatedVisitCost: poi.EstimatedVisitCost,
            OpeningHours: poi.OpeningHours,
            CategoryName: poi.CategoryName,
            IndoorOutdoor: poi.IndoorOutdoor,
            HasShelter: poi.HasShelter,
            ScenicScore: poi.ScenicScore,
            PhotoRating: poi.PhotoRating,
            Tags: poi.Tags)).ToArray();

        var points = new List<RoutePoint>(candidates.Length + 2) { data.Start };
        points.AddRange(candidates.Select(candidate => candidate.Location));
        points.Add(data.End);

        var matrix = CreateMatrix(points);
        var input = new GenerationInput(
            data.StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById(data.TimeZoneId),
            data.Start,
            data.End,
            data.AvailableMinutes,
            data.TransportMode,
            data.RestPreference,
            data.BudgetVnd,
            candidates,
            data.MandatoryPoiIds);
        var indices = candidates
            .Select((candidate, index) => (candidate.Id, MatrixIndex: index + 1))
            .ToDictionary(pair => pair.Id, pair => pair.MatrixIndex);

        return new GoldenScenario(data.Id, data.Source, data.ExpectedFeasible, input, matrix, candidates, indices);
    }

    public static TravelerProfile? CreateProfile(BenchmarkPersonaData persona, DateTimeOffset nowUtc)
    {
        if (persona.Id == "DefaultGeneralist")
        {
            return null;
        }

        var profile = TravelerProfile.CreateEmpty(1001, nowUtc);
        profile.ReplacePreferences(
            JsonSerializer.Serialize(persona.Tags),
            TransportMode.Motorbike,
            persona.Pace,
            persona.Risk,
            foodPreferencesJson: null,
            nowUtc);
        return profile;
    }

    public static PersonalizationOptions CreatePersonalizationOptions() => new()
    {
        Version = "2026.2",
        Pace = new Dictionary<TravelerPace, PaceParams>
        {
            [TravelerPace.Relaxed] = new(0.018, 3, 200, 90),
            [TravelerPace.Moderate] = new(0.012, 5, 300, 120),
            [TravelerPace.Fast] = new(0.008, 8, 450, 180)
        },
        Mu = 0.005,
        GammaDefault = 0.7,
        GammaLoved = 0.9,
        RhoOutdoor = 0.25,
        RhoRest = 0.0,
        RhoStops = 0.30,
        RhoEffortPerHour = 0.05,
        TagToCategory = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["beach"] = [PoiCategoryNames.Beach],
            ["photography"] = [PoiCategoryNames.Culture],
            ["heritage"] = [PoiCategoryNames.Culture],
            ["museum"] = [PoiCategoryNames.Museum],
            ["nature"] = [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction],
            ["natural attraction"] = [PoiCategoryNames.NaturalAttraction],
            ["theme park"] = [PoiCategoryNames.ThemePark],
            ["water park"] = [PoiCategoryNames.WaterPark],
            ["food"] = [PoiCategoryNames.FoodAndDrink],
            ["coffee"] = [PoiCategoryNames.FoodAndDrink],
            ["cuisine"] = [PoiCategoryNames.FoodAndDrink],
            ["culinary"] = [PoiCategoryNames.FoodAndDrink],
            ["culture"] = [PoiCategoryNames.Culture],
            ["history"] = [PoiCategoryNames.Culture],
            ["art"] = [PoiCategoryNames.Culture],
            ["adventure"] = [PoiCategoryNames.NaturalAttraction],
            ["hiking"] = [PoiCategoryNames.NaturalAttraction],
            ["outdoor"] = [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction, PoiCategoryNames.Beach],
            ["sightseeing"] = [PoiCategoryNames.Culture],
            ["relaxation"] = [PoiCategoryNames.Beach]
        }
    };

    internal static PersonalizationRankingOptions CreateV3bRankingOptions() => new()
    {
        CategoryAffinityWeight = 0.300m,
        TagAffinityWeight = 0.200m,
        BehaviorAffinityWeight = 0.250m,
        ScenicQualityWeight = 0.125m,
        PhotoQualityWeight = 0.125m
    };

    private static RouteDurationMatrix CreateMatrix(IReadOnlyList<RoutePoint> points)
    {
        var minutes = new int[points.Count, points.Count];
        var meters = new int[points.Count, points.Count];
        for (var from = 0; from < points.Count; from++)
        {
            for (var to = 0; to < points.Count; to++)
            {
                if (from == to)
                {
                    continue;
                }

                var distanceKm = HaversineKm(points[from], points[to]);
                meters[from, to] = (int)Math.Round(distanceKm * WindingFactor * 1000.0);
                minutes[from, to] = Math.Max(2, (int)Math.Ceiling(distanceKm * WindingFactor / AverageSpeedKph * 60.0));
            }
        }

        return RouteDurationMatrix.Create(minutes, null, meters);
    }

    private static double HaversineKm(RoutePoint from, RoutePoint to)
    {
        var latitudeFrom = (double)from.Latitude * Math.PI / 180.0;
        var longitudeFrom = (double)from.Longitude * Math.PI / 180.0;
        var latitudeTo = (double)to.Latitude * Math.PI / 180.0;
        var longitudeTo = (double)to.Longitude * Math.PI / 180.0;
        var latitudeDelta = latitudeTo - latitudeFrom;
        var longitudeDelta = longitudeTo - longitudeFrom;
        var haversine = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(latitudeFrom) * Math.Cos(latitudeTo)
            * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        var centralAngle = 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
        return EarthRadiusKm * centralAngle;
    }
}