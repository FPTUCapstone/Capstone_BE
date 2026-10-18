using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.TimeOfDay;

public class TimeFitFactorTests
{
    private static readonly TimeZoneInfo DanangTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    // Ngày 21/06/2026 bắt đầu lúc 06:00 địa phương
    private static readonly DateTimeOffset StartUtc = new(2026, 6, 20, 23, 0, 0, TimeSpan.Zero);

    private static (RoutingContext Context, GenerationCandidate Candidate) CreateContext(
        GenerationCandidate candidate,
        int availableMinutes = 900)
    {
        var input = new GenerationInput(
            StartAtUtc: StartUtc,
            TimeZone: DanangTz,
            Start: new RoutePoint(16.05m, 108.20m),
            End: new RoutePoint(16.05m, 108.20m),
            AvailableMinutes: availableMinutes,
            TransportMode: TransportMode.Motorbike,
            RestPreference: RestPreference.None,
            BudgetVnd: null,
            Candidates: [candidate],
            MandatoryPoiIds: []);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 10 },
            { 10, 0, 10 },
            { 10, 10, 0 }
        });

        var options = new SchedulingGenerationOptions();
        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, [candidate], new Dictionary<long, int> { [candidate.Id] = 1 }, options);
        return (ctx, candidate);
    }

    [Fact]
    public void R1_Sunrise_FullOverlap_Yields_BonusFactor()
    {
        var beach = new GenerationCandidate(
            1, "Beach", new RoutePoint(16.05m, 108.20m), 30, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Beach", IndoorOutdoor: IndoorOutdoorType.Indoor); // Indoor để R2 không áp dụng

        var (ctx, _) = CreateContext(beach);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // Bình minh ~ 05:16. Cửa sổ R1: [04:31, 06:16].
        // Ghé lúc 05:30 đến 06:00 (phút mô hình: -30 đến 0) hoàn toàn nằm trong R1.
        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 5, 30, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 6, 0, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(1.0 + 0.3 * 0.6, 1e-4);
    }

    [Fact]
    public void R1_SunriseFactor_Zero_Yields_No_Bonus()
    {
        var beach = new GenerationCandidate(
            1, "Beach", new RoutePoint(16.05m, 108.20m), 30, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Beach", IndoorOutdoor: IndoorOutdoorType.Indoor);

        var (ctx, _) = CreateContext(beach);
        // SunriseFactor = 0 -> không thưởng
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.0);

        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 5, 30, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 6, 0, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void R2_CoolMorning_FullOverlap_Yields_1_10()
    {
        var park = new GenerationCandidate(
            2, "Park", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            IndoorOutdoor: IndoorOutdoorType.Outdoor, HasShelter: true); // HasShelter để R3 không phạt

        var (ctx, _) = CreateContext(park);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // 07:00 đến 08:00 nằm trọn trong [06:00, 09:30]
        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 7, 0, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 8, 0, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(1.10, 1e-4);
    }

    [Fact]
    public void R3_Midday_Outdoor_NoShelter_FullOverlap_Yields_0_70()
    {
        var outdoor = new GenerationCandidate(
            3, "Square", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            IndoorOutdoor: IndoorOutdoorType.Outdoor, HasShelter: false);

        var (ctx, _) = CreateContext(outdoor);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // 12:00 đến 13:00 nằm trọn trong [11:00, 14:30]
        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 12, 0, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 13, 0, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(0.70, 1e-4);
    }

    [Fact]
    public void R3_Midday_Mixed_FullOverlap_Yields_0_85()
    {
        var mixed = new GenerationCandidate(
            4, "Mixed Market", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            IndoorOutdoor: IndoorOutdoorType.Mixed);

        var (ctx, _) = CreateContext(mixed);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 12, 0, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 13, 0, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(0.85, 1e-4);
    }

    [Fact]
    public void R4_Sunset_FullOverlap_Yields_1_20()
    {
        var viewpoint = new GenerationCandidate(
            5, "Viewpoint", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            ScenicScore: 9.0m, IndoorOutdoor: IndoorOutdoorType.Indoor); // Indoor để R6 không áp dụng

        var (ctx, _) = CreateContext(viewpoint);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // Hoàng hôn ~ 18:22. Cửa sổ R4: [16:52, 18:37].
        // Ghé lúc 17:15 đến 18:15 nằm trọn trong R4.
        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 17, 15, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 18, 15, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(1.20, 1e-4);
    }

    [Fact]
    public void R6_Night_FullOverlap_Yields_0_30()
    {
        var nightForest = new GenerationCandidate(
            6, "Night Forest", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Nature", IndoorOutdoor: IndoorOutdoorType.Outdoor);

        var (ctx, _) = CreateContext(nightForest, availableMinutes: 960);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // Hoàng hôn ~ 18:22. Cửa sổ R6: từ 18:52 đến hết horizon.
        // Ghé lúc 19:30 đến 20:30 nằm trọn trong R6.
        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 19, 30, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 20, 30, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);
        factor.Should().BeApproximately(0.30, 1e-4);
    }

    [Fact]
    public void Beach_Midday_Overlap_0_75_Yields_0_775()
    {
        // Bài test quy chuẩn từ prompt:
        // biển 10:30–12:30, R3 = 0.70, overlap 0.75 → Factor = 0.775.
        var beach = new GenerationCandidate(
            10, "My Khe Beach", new RoutePoint(16.05m, 108.20m), 120, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Beach", IndoorOutdoor: IndoorOutdoorType.Outdoor, HasShelter: false);

        var (ctx, _) = CreateContext(beach);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // Ghé từ 10:30 đến 12:30
        var startMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 10, 30, 0), ceiling: false);
        var endMin = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 12, 30, 0), ceiling: false);

        var factor = provider.Factor(0, startMin, endMin);

        // Overlap với R3 [11:00, 14:30] là [11:00, 12:30] = 90 phút / 120 phút = 0.75
        // 1 + (0.70 - 1) * 0.75 = 1 - 0.225 = 0.775
        factor.Should().BeApproximately(0.775, 1e-4);
    }

    [Fact]
    public void Zero_Allocation_10000_Calls_To_Factor()
    {
        var beach = new GenerationCandidate(
            10, "My Khe Beach", new RoutePoint(16.05m, 108.20m), 120, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Beach", IndoorOutdoor: IndoorOutdoorType.Outdoor, HasShelter: false);

        var (ctx, _) = CreateContext(beach);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        // Warm up
        for (var i = 0; i < 100; i++)
        {
            _ = provider.Factor(0, 100, 160);
        }

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 10_000; i++)
        {
            _ = provider.Factor(0, 100, 160);
        }

        var bytesAfter = GC.GetAllocatedBytesForCurrentThread();

        (bytesAfter - bytesBefore).Should().Be(0, "Factor must allocate zero heap bytes during solve");
    }

    [Fact]
    public void MaxFactor_Dominates_Factor_Across_Time_Windows()
    {
        var beach = new GenerationCandidate(
            10, "My Khe Beach", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Beach", IndoorOutdoor: IndoorOutdoorType.Outdoor, HasShelter: false, ScenicScore: 9.5m);

        var (ctx, _) = CreateContext(beach);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var maxFactor = provider.MaxFactor(0);
        maxFactor.Should().BeLessOrEqualTo(1.5);
        maxFactor.Should().BeGreaterThan(1.0);

        // Duyệt qua mọi khung 15 phút từ 0 đến 720 phút
        for (var m = 0; m <= 660; m += 15)
        {
            var factor = provider.Factor(0, m, m + 60);
            factor.Should().BeLessOrEqualTo(maxFactor + 1e-9, $"MaxFactor ({maxFactor}) must dominate Factor ({factor}) at start {m}");
        }
    }
}
