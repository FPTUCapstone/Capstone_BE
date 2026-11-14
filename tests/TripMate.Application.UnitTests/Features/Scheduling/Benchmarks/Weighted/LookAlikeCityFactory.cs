using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>
/// A simulated city for the behavior-learning benchmark (UC-10 item 6): 54 stops in seven families of look-alike
/// places (bridges, museums, pagodas, markets, beaches, hikes, parks), named after real places around Da Nang.
/// The real 19-POI fixture has too few look-alikes to tell semantic learning from "do not repeat what was removed":
/// removing three bridges there removes every bridge. Here a traveler who removes three bridges still has five more
/// to be offered, unless the ranking understands that they are alike.
/// <para>
/// Families share the broad categories the rules see ("Culture" covers bridges, pagodas and markets), positions are
/// interleaved so no family sits in one corner of the city, and scenic and photo scores are spread evenly. Only
/// names and tags tell the families apart.
/// </para>
/// </summary>
public static class LookAlikeCityFactory
{
    public const string ScenarioId = "simulated-look-alike-city";

    private sealed record Family(
        string Category,
        int VisitMinutes,
        decimal Cost,
        IndoorOutdoorType Exposure,
        bool HasShelter,
        string[] Tags,
        string[] Names);

    private static readonly Family[] Families =
    [
        new(PoiCategoryNames.Culture, 30, 0m, IndoorOutdoorType.Outdoor, false,
            ["architecture", "photography", "sightseeing"],
            ["Cầu Rồng", "Cầu Sông Hàn", "Cầu Trần Thị Lý", "Cầu Thuận Phước", "Cầu Nguyễn Văn Trỗi", "Cầu Tình Yêu", "Cầu Tiên Sơn", "Cầu Cẩm Lệ"]),
        new(PoiCategoryNames.Museum, 90, 50_000m, IndoorOutdoorType.Indoor, true,
            ["culture", "museum", "history", "art"],
            ["Bảo tàng Điêu khắc Chăm", "Bảo tàng Đà Nẵng", "Bảo tàng Mỹ thuật Đà Nẵng", "Bảo tàng Quân khu 5", "Bảo tàng Hồ Chí Minh", "Bảo tàng Đồng Đình", "Bảo tàng Văn hóa Phật giáo", "Nhà trưng bày Hoàng Sa"]),
        new(PoiCategoryNames.Culture, 60, 0m, IndoorOutdoorType.Mixed, true,
            ["heritage", "culture", "spiritual"],
            ["Chùa Linh Ứng Bãi Bụt", "Chùa Linh Ứng Non Nước", "Chùa Tam Thai", "Chùa Quán Thế Âm", "Chùa Pháp Lâm", "Chùa Nam Sơn", "Chùa Bà Đa", "Chùa An Long"]),
        new(PoiCategoryNames.Culture, 60, 0m, IndoorOutdoorType.Indoor, true,
            ["shopping", "food", "culture"],
            ["Chợ Hàn", "Chợ Cồn", "Chợ Bắc Mỹ An", "Chợ đêm Sơn Trà", "Chợ đêm Helio", "Chợ Đống Đa", "Chợ Hòa Khánh", "Chợ Mới"]),
        new(PoiCategoryNames.Beach, 120, 0m, IndoorOutdoorType.Outdoor, false,
            ["beach", "relaxation", "nature", "outdoor"],
            ["Bãi biển Mỹ Khê", "Bãi biển Phạm Văn Đồng", "Bãi biển Non Nước", "Bãi biển Bắc Mỹ An", "Bãi Rạng", "Bãi Bụt", "Bãi biển Xuân Thiều", "Bãi biển Nam Ô"]),
        new(PoiCategoryNames.NaturalAttraction, 120, 20_000m, IndoorOutdoorType.Outdoor, false,
            ["nature", "adventure", "outdoor", "hiking", "scenic"],
            ["Ngũ Hành Sơn - Thủy Sơn", "Đỉnh Bàn Cờ", "Đèo Hải Vân", "Suối Lương", "Bán đảo Sơn Trà", "Ghềnh Bàng", "Rạn Nam Ô", "Hồ Xanh"]),
        new(PoiCategoryNames.Nature, 45, 0m, IndoorOutdoorType.Outdoor, false,
            ["relaxation", "nature"],
            ["Công viên APEC", "Công viên Biển Đông", "Công viên 29 tháng 3", "Công viên Châu Á", "Công viên Thanh Niên", "Vườn tượng Đà Nẵng"]),
    ];

    public static CityBenchmarkScenarioData Create()
    {
        var hotel = new RoutePoint(16.0610m, 108.2250m);
        var openingHours = new[] { new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0)) };
        int longest = Families.Max(family => family.Names.Length);
        var pois = new List<BenchmarkPoiData>();

        // Interleave the families (first of each, then second of each, ...), so a stop's position, scenic score
        // and photo score depend on its place in this sequence, never on its family.
        for (var member = 0; member < longest; member++)
        {
            foreach (Family family in Families.Where(family => member < family.Names.Length))
            {
                int index = pois.Count;
                pois.Add(new BenchmarkPoiData(
                    Id: index + 1,
                    Name: family.Names[member],
                    Location: Position(hotel, index),
                    CategoryName: family.Category,
                    VisitDurationMinutes: family.VisitMinutes,
                    EstimatedVisitCost: family.Cost,
                    IndoorOutdoor: family.Exposure,
                    HasShelter: family.HasShelter,
                    ScenicScore: 6m + (index % 3),
                    PhotoRating: 6m + ((index + 1) % 3),
                    Tags: family.Tags,
                    OpeningHours: openingHours));
            }
        }

        return new CityBenchmarkScenarioData(
            Id: ScenarioId,
            Source: "Simulated for the UC-10 behavior-learning benchmark: real place names around Da Nang, synthetic positions and attributes",
            ExpectedFeasible: true,
            StartAtUtc: new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
            TimeZoneId: "Asia/Ho_Chi_Minh",
            Start: hotel,
            End: hotel,
            AvailableMinutes: 480,
            TransportMode: TransportMode.Motorbike,
            RestPreference: RestPreference.None,
            BudgetVnd: 1_000_000m,
            MandatoryPoiIds: [],
            Pois: pois,
            Personas: []);
    }

    /// <summary>A sunflower spiral around the hotel, one to six kilometres out: deterministic and evenly spread.</summary>
    private static RoutePoint Position(RoutePoint hotel, int index)
    {
        const double goldenAngle = 2.39996322972865332;
        double radiusKm = 1d + (5d * Math.Sqrt((index + 1) / 54d));
        double angle = index * goldenAngle;
        double latitude = (double)hotel.Latitude + (radiusKm * Math.Cos(angle) / 110.574);
        double longitude = (double)hotel.Longitude
            + (radiusKm * Math.Sin(angle) / (111.320 * Math.Cos((double)hotel.Latitude * Math.PI / 180d)));
        return new RoutePoint(Math.Round((decimal)latitude, 6), Math.Round((decimal)longitude, 6));
    }
}