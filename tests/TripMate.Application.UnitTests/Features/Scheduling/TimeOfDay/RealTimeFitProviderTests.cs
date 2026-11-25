using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.TimeOfDay;

public class RealTimeFitProviderTests
{
    private static readonly TimeZoneInfo DanangTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    // Ngày 21/06/2026 bắt đầu lúc 06:00 địa phương (23:00 ngày 20/06 UTC)
    private static readonly DateTimeOffset StartUtc = new(2026, 6, 20, 23, 0, 0, TimeSpan.Zero);

    private static (RoutingContext Context, GenerationCandidate Candidate) CreateContextWithCandidate(
        GenerationCandidate candidate,
        int availableMinutes = 720)
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
    public void RoutingContext_ToMinutes_Converts_LocalTime_To_Model_Minutes()
    {
        var cand = new GenerationCandidate(
            1, "Test POI", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null);
        var (ctx, _) = CreateContextWithCandidate(cand);

        // StartAtUtc tương ứng 06:00 sáng ngày 21/06 địa phương.
        // 06:00 -> 0 phút
        var m0 = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 6, 0, 0), ceiling: false);
        m0.Should().Be(0);

        // 07:30 -> 90 phút
        var m90 = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 7, 30, 0), ceiling: false);
        m90.Should().Be(90);

        // 18:00 -> 720 phút
        var m720 = RoutingContext.ToMinutes(ctx.Input, new DateTime(2026, 6, 21, 18, 0, 0), ceiling: false);
        m720.Should().Be(720);
    }

    [Fact]
    public void R1_Sunrise_Window_Computed_In_Model_Minutes_For_Beach()
    {
        var beach = new GenerationCandidate(
            10, "My Khe Beach", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Beach", IndoorOutdoor: IndoorOutdoorType.Outdoor);

        var (ctx, _) = CreateContextWithCandidate(beach);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var windows = provider.GetWindows(0);

        // Ngày 21/6 tại Đà Nẵng: bình minh ~ 05:16.
        // Cửa sổ R1: [sunrise - 45', sunrise + 60'] = ~ [04:31, 06:16].
        // Vì bắt đầu lúc 06:00, 04:31 địa phương đổi ra phút mô hình là số âm (~ -89 phút).
        // 06:16 đổi ra phút mô hình là ~ +16 phút.
        var r1Window = windows.FirstOrDefault(w => Math.Abs(w.Factor - (1.0 + 0.3 * 0.6)) < 1e-6);
        r1Window.Should().NotBeNull();
        r1Window.Factor.Should().BeApproximately(1.18, 1e-6);

        var (sunrise, _) = SunService.Instance.Get(16.05, 108.20, new DateOnly(2026, 6, 21), DanangTz);
        var sunriseDt = new DateTime(2026, 6, 21).Add(sunrise.ToTimeSpan());
        var expectedOpen = RoutingContext.ToMinutes(ctx.Input, sunriseDt.AddMinutes(-45), ceiling: false);
        var expectedClose = RoutingContext.ToMinutes(ctx.Input, sunriseDt.AddMinutes(60), ceiling: false);

        r1Window.Open.Should().Be(expectedOpen);
        r1Window.Close.Should().Be(expectedClose);
    }

    [Fact]
    public void R2_CoolMorning_Window_Computed_In_Model_Minutes_For_Outdoor()
    {
        var park = new GenerationCandidate(
            20, "Son Tra Nature Park", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Nature", IndoorOutdoor: IndoorOutdoorType.Outdoor);

        var (ctx, _) = CreateContextWithCandidate(park);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var windows = provider.GetWindows(0);
        // R2: [06:00, 09:30]
        // 06:00 -> 0 phút mô hình, 09:30 -> 210 phút mô hình. Factor = 1.10
        var r2Window = windows.FirstOrDefault(w => Math.Abs(w.Factor - 1.10) < 1e-6);
        r2Window.Should().NotBeNull();
        r2Window.Open.Should().Be(0);
        r2Window.Close.Should().Be(210);
    }

    [Fact]
    public void R3_Midday_Outdoor_NoShelter_Factor_Is_0_70()
    {
        var outdoor = new GenerationCandidate(
            30, "Outdoor Open Area", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            IndoorOutdoor: IndoorOutdoorType.Outdoor, HasShelter: false);

        var (ctx, _) = CreateContextWithCandidate(outdoor);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var windows = provider.GetWindows(0);
        // R3: [11:00, 14:30]
        // 11:00 -> 300 phút, 14:30 -> 510 phút. Factor = 0.70
        var r3Window = windows.FirstOrDefault(w => Math.Abs(w.Factor - 0.70) < 1e-6);
        r3Window.Should().NotBeNull();
        r3Window.Open.Should().Be(300);
        r3Window.Close.Should().Be(510);
    }

    [Fact]
    public void R3_Midday_Mixed_Factor_Is_0_85()
    {
        var mixed = new GenerationCandidate(
            31, "Mixed Venue", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            IndoorOutdoor: IndoorOutdoorType.Mixed);

        var (ctx, _) = CreateContextWithCandidate(mixed);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var windows = provider.GetWindows(0);
        var r3Window = windows.FirstOrDefault(w => Math.Abs(w.Factor - 0.85) < 1e-6);
        r3Window.Should().NotBeNull();
        r3Window.Open.Should().Be(300);
        r3Window.Close.Should().Be(510);
    }

    [Fact]
    public void R4_Sunset_Window_Computed_In_Model_Minutes_For_High_Scenic()
    {
        var scenic = new GenerationCandidate(
            40, "Hai Van Pass Viewpoint", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            ScenicScore: 9.0m, IndoorOutdoor: IndoorOutdoorType.Outdoor);

        var (ctx, _) = CreateContextWithCandidate(scenic);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var windows = provider.GetWindows(0);
        // R4: [sunset - 90', sunset + 15'], Factor = 1.20
        var r4Window = windows.FirstOrDefault(w => Math.Abs(w.Factor - 1.20) < 1e-6);
        r4Window.Should().NotBeNull();

        var (_, sunset) = SunService.Instance.Get(16.05, 108.20, new DateOnly(2026, 6, 21), DanangTz);
        var sunsetDt = new DateTime(2026, 6, 21).Add(sunset.ToTimeSpan());
        var expectedOpen = RoutingContext.ToMinutes(ctx.Input, sunsetDt.AddMinutes(-90), ceiling: false);
        var expectedClose = RoutingContext.ToMinutes(ctx.Input, sunsetDt.AddMinutes(15), ceiling: false);

        r4Window.Open.Should().Be(expectedOpen);
        r4Window.Close.Should().Be(expectedClose);
    }

    [Fact]
    public void R6_Night_Window_Computed_In_Model_Minutes_For_Outdoor_Nature()
    {
        var jungle = new GenerationCandidate(
            60, "Ba Na Jungle", new RoutePoint(16.05m, 108.20m), 60, null, [], 1.0m, 1.0m, null, null, null,
            CategoryName: "Natural attraction", IndoorOutdoor: IndoorOutdoorType.Outdoor);

        var (ctx, _) = CreateContextWithCandidate(jungle, availableMinutes: 900);
        var provider = new RealTimeFitProvider(ctx, sunriseFactor: 0.6);

        var windows = provider.GetWindows(0);
        // R6: [sunset + 30', hết horizon], Factor = 0.30
        var r6Window = windows.FirstOrDefault(w => Math.Abs(w.Factor - 0.30) < 1e-6);
        r6Window.Should().NotBeNull();

        var (_, sunset) = SunService.Instance.Get(16.05, 108.20, new DateOnly(2026, 6, 21), DanangTz);
        var sunsetDt = new DateTime(2026, 6, 21).Add(sunset.ToTimeSpan());
        var expectedOpen = RoutingContext.ToMinutes(ctx.Input, sunsetDt.AddMinutes(30), ceiling: false);

        r6Window.Open.Should().Be(expectedOpen);
        r6Window.Close.Should().Be(ctx.Horizon);
    }
}