using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

using Xunit.Abstractions;

namespace TripMate.Application.UnitTests.Features.Scheduling.Csp;

/// <summary>
/// So CSP với phép duyệt vét cạn dùng <see cref="ItineraryScheduleEvaluator"/> làm chuẩn khi có điểm nghỉ (Auto,
/// Frequent): mục tiêu được tính trên lịch thật sau khi chèn nghỉ. Mô hình CSP không chứa điểm nghỉ nên không
/// chắc chắn tối ưu ở đây; các test này ghi lại mức sai lệch và chặn hồi quy.
/// </summary>
public class CspRestOptimalityTests(ITestOutputHelper output)
{
    private const int InstancesPerPreference = 80;

    private static readonly Dictionary<RestPreference, int> MaxWorse = new()
    {
        [RestPreference.Auto] = 0,
        [RestPreference.Frequent] = 0,
    };

    private static readonly Dictionary<RestPreference, int> MaxMissed = new()
    {
        [RestPreference.Auto] = 0,
        [RestPreference.Frequent] = 0,
    };

    private static readonly SchedulingGenerationOptions Options = new();

    private static readonly CspOptions Exhaustive = new()
    {
        MaxStops = 99,
        MaxOptionalDomainSize = 99,
        MaxNodes = 10_000_000,
        TimeLimitMilliseconds = 60_000,
    };

    [Theory]
    [InlineData(RestPreference.Auto)]
    [InlineData(RestPreference.Frequent)]
    public void Solve_StaysCloseToTheEvaluatorOptimum_WhenRestStopsAreInserted(RestPreference restPreference)
    {
        var evaluator = new ItineraryScheduleEvaluator(Options);
        int feasible = 0, optimal = 0, worse = 0, missed = 0;

        for (var seed = 1; seed <= InstancesPerPreference; seed++)
        {
            var (input, matrix) = RandomInstance(seed, restPreference);
            var (candidates, indices) = Index(input);
            var comparer = CspProductRuleOracle.Comparer(input, matrix);
            var optimum = EvaluatorOptimum(evaluator, input, matrix, candidates, indices, comparer);

            var result = new CspItinerarySolver(evaluator, Options, Exhaustive).Solve(input, matrix, candidates, indices);

            if (result.Schedule is not null)
            {
                optimum.Should().NotBeNull("the solver returned a valid schedule, so the exhaustive search finds one too");
                comparer.Compare(result.Schedule, optimum).Should().BeGreaterThanOrEqualTo(0, "no schedule beats the exhaustive optimum");
            }

            if (optimum is null)
            {
                result.Schedule.Should().BeNull();
                continue;
            }

            feasible++;
            if (result.Schedule is null)
            {
                missed++;
            }
            else if (comparer.Compare(result.Schedule, optimum) == 0)
            {
                optimal++;
            }
            else
            {
                worse++;
            }
        }

        output.WriteLine($"{restPreference}: {feasible} feasible, {optimal} optimal, {worse} worse, {missed} without a CSP schedule");

        // Đo trên chính các instance này theo product rule (R1), sau khi chỉ siết incumbent bằng lời giải đã qua evaluator
        // khi có điểm nghỉ (R9). Không được tệ hơn mức này; ngưỡng chỉ được giảm.
        feasible.Should().BeGreaterThan(InstancesPerPreference / 2, "the generator must produce mostly feasible instances");
        missed.Should().BeLessThanOrEqualTo(MaxMissed[restPreference]);
        worse.Should().BeLessThanOrEqualTo(MaxWorse[restPreference]);
    }

    /// <summary>
    /// R9: điểm nghỉ là một địa điểm thật (quán cà phê) mà evaluator chèn vào giữa chặng a → b, nên phút di chuyển của
    /// lịch là a → quán + quán → b. Trên ma trận không metric, đường vòng qua quán có thể ngắn hơn đi thẳng, tức lịch thật
    /// đi ít hơn mô hình CSP. Cận và việc siết incumbent phải tính khoản tiết kiệm đó, nếu không sẽ cắt mất lịch tốt nhất.
    /// </summary>
    [Theory]
    [InlineData(RestPreference.Auto)]
    [InlineData(RestPreference.Frequent)]
    public void Solve_StaysOptimal_WhenRestStopsArePlacesOnShortcuts(RestPreference restPreference)
    {
        var evaluator = new ItineraryScheduleEvaluator(Options);
        var worse = new List<string>();
        var feasible = 0;

        for (var seed = 1; seed <= InstancesPerPreference; seed++)
        {
            var (input, matrix) = RandomInstanceWithRestPlaces(seed, restPreference);
            var (candidates, indices) = Index(input);
            var comparer = CspProductRuleOracle.Comparer(input, matrix);
            var optimum = EvaluatorOptimum(evaluator, input, matrix, candidates, indices, comparer);

            var result = new CspItinerarySolver(evaluator, Options, Exhaustive).Solve(input, matrix, candidates, indices);

            if (optimum is null)
            {
                continue;
            }

            feasible++;
            if (result.Schedule is null || comparer.Compare(result.Schedule, optimum) != 0)
            {
                worse.Add($"seed {seed}: csp [{string.Join(",", result.Schedule?.VisitPoiIds ?? [])}] travel {result.Schedule?.TotalMatrixTravelMinutes}, "
                    + $"optimum [{string.Join(",", optimum.VisitPoiIds)}] travel {optimum.TotalMatrixTravelMinutes}");
            }
        }

        output.WriteLine($"{restPreference}: {feasible} feasible, {worse.Count} worse");
        feasible.Should().BeGreaterThan(InstancesPerPreference / 2, "the generator must produce mostly feasible instances");
        worse.Should().BeEmpty();
    }

    /// <summary>
    /// Khi không có điểm nghỉ thì mô hình CSP trùng lịch thật: CSP phải cho đúng lịch tối ưu theo product rule.
    /// </summary>
    [Fact]
    public void Solve_MatchesTheEvaluatorOptimum_WithoutRest()
    {
        var evaluator = new ItineraryScheduleEvaluator(Options);
        for (var seed = 1; seed <= InstancesPerPreference / 2; seed++)
        {
            var (input, matrix) = RandomInstance(seed, RestPreference.None);
            var (candidates, indices) = Index(input);
            var comparer = CspProductRuleOracle.Comparer(input, matrix);
            var optimum = EvaluatorOptimum(evaluator, input, matrix, candidates, indices, comparer);

            var result = new CspItinerarySolver(evaluator, Options, Exhaustive).Solve(input, matrix, candidates, indices);

            if (optimum is null)
            {
                result.Schedule.Should().BeNull();
                result.ProvedInfeasible.Should().BeTrue();
            }
            else
            {
                result.Schedule.Should().NotBeNull();
                comparer.Compare(result.Schedule, optimum).Should().Be(0, $"seed {seed}");
            }
        }
    }

    /// <summary>
    /// Hai lần chạy (có và không dự phòng nghỉ) dùng chung một giới hạn số nút: tổng số nút đã duyệt không vượt
    /// MaxNodes. Budget được kiểm tra trước khi đếm nút, nên nút bị từ chối không được tính vào số liệu.
    /// </summary>
    [Fact]
    public void Solve_SharesOneNodeBudget_AcrossTheReservedAndRelaxedRuns()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(40, 3, 5, RestPreference.Frequent);
        var (candidates, indices) = Index(input);
        var limits = new CspOptions { MaxNodes = 500 };

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, limits)
            .Solve(input, matrix, candidates, indices);

        result.Statistics.UsedRelaxedRestModel.Should().BeTrue();
        result.Statistics.NodeLimitReached.Should().BeTrue();
        result.Statistics.NodesExpanded.Should().Be(limits.MaxNodes);
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) RandomInstance(int seed, RestPreference restPreference)
    {
        var random = new Random((seed * 7919) + (int)restPreference);
        var candidateCount = random.Next(4, 7);
        var mandatoryCount = random.Next(0, 3);
        var candidates = new List<GenerationCandidate>(candidateCount);
        for (var i = 1; i <= candidateCount; i++)
        {
            var hours = random.Next(4) == 0
                ? new[] { new GenerationOpeningHours(2, new TimeOnly(random.Next(8, 13), 0), new TimeOnly(random.Next(14, 20), 0)) }
                : null;
            candidates.Add(OptionalRouteOptimizationScenarios.CreateCandidate(
                id: i,
                name: $"POI_{i}",
                visitDurationMinutes: random.Next(2, 9) * 15,
                cost: random.Next(1, 4) * 10_000m,
                effectiveDesirabilityScore: Math.Round((decimal)random.NextDouble(), 2) + 0.01m,
                openingHours: hours));
        }

        // Ma trận bất đối xứng, khoảng một phần tư số cạnh rất ngắn để vi phạm bất đẳng thức tam giác.
        var points = candidateCount + 2;
        var data = new int[points, points];
        for (var i = 0; i < points; i++)
        {
            for (var j = 0; j < points; j++)
            {
                data[i, j] = i == j ? 0 : random.Next(4) == 0 ? random.Next(1, 5) : random.Next(10, 50);
            }
        }

        var availableMinutes = restPreference == RestPreference.Auto ? random.Next(300, 481) : random.Next(180, 481);
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes,
            candidates,
            mandatoryPoiIds: candidates.Take(mandatoryCount).Select(c => c.Id).ToArray(),
            restPreference: restPreference,
            budgetVnd: random.Next(2) == 0 ? null : random.Next(4, 12) * 10_000m);
        return (input, RouteDurationMatrix.Create(data));
    }

    /// <summary>
    /// Như <see cref="RandomInstance"/>, cộng thêm hai quán cà phê (điểm nghỉ thật, luôn mở cửa) mà nhiều cạnh tới và đi rất
    /// ngắn, nên đường vòng qua quán thường ngắn hơn đi thẳng giữa hai điểm tham quan.
    /// </summary>
    private static (GenerationInput Input, RouteDurationMatrix Matrix) RandomInstanceWithRestPlaces(int seed, RestPreference restPreference)
    {
        var (baseInput, _) = RandomInstance(seed, restPreference);
        var random = new Random((seed * 104_729) + (int)restPreference);
        var cafes = new[] { 901L, 902L }
            .Select(id => OptionalRouteOptimizationScenarios.CreateCandidate(id, $"Cafe_{id}", 30, 0m, 0.1m) with { CategoryName = "Cafe" })
            .ToArray();
        var candidates = baseInput.Candidates.Concat(cafes).ToArray();

        var points = candidates.Length + 2;
        var data = new int[points, points];
        var cafeIndices = new[] { points - 3, points - 2 };
        for (var i = 0; i < points; i++)
        {
            for (var j = 0; j < points; j++)
            {
                var touchesCafe = cafeIndices.Contains(i) || cafeIndices.Contains(j);
                data[i, j] = i == j ? 0
                    : touchesCafe && random.Next(2) == 0 ? random.Next(1, 4)
                    : random.Next(4) == 0 ? random.Next(1, 5) : random.Next(10, 50);
            }
        }

        return (baseInput with { Candidates = candidates }, RouteDurationMatrix.Create(data));
    }

    /// <summary>
    /// Lịch tốt nhất theo product rule trên mọi thứ tự điểm đến chứa đủ điểm bắt buộc mà evaluator chấp nhận. Chỉ xét
    /// điểm mà bộ giải có thể xếp (nút của <see cref="RoutingContext"/>; điểm nghỉ không phải điểm tham quan).
    /// </summary>
    private static EvaluatedItinerarySchedule? EvaluatorOptimum(
        ItineraryScheduleEvaluator evaluator,
        GenerationInput input,
        RouteDurationMatrix matrix,
        GenerationCandidate[] candidates,
        Dictionary<long, int> indices,
        ScheduleGlobalComparator comparer)
    {
        var schedulable = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting)
            .Nodes.Select(node => node.Candidate.Id)
            .ToHashSet();
        EvaluatedItinerarySchedule? best = null;
        var sequence = new List<GenerationCandidate>();
        var used = new bool[candidates.Length];

        void Visit()
        {
            if (sequence.Count > 0
                && input.MandatoryPoiIds.All(id => sequence.Any(c => c.Id == id))
                && evaluator.Evaluate(input, sequence.ToArray(), matrix, candidates, indices, CancellationToken.None) is { } schedule
                && (best is null || comparer.Compare(schedule, best) < 0))
            {
                best = schedule;
            }

            for (var i = 0; i < candidates.Length; i++)
            {
                if (!used[i] && schedulable.Contains(candidates[i].Id))
                {
                    used[i] = true;
                    sequence.Add(candidates[i]);
                    Visit();
                    sequence.RemoveAt(sequence.Count - 1);
                    used[i] = false;
                }
            }
        }

        Visit();
        return best;
    }

    private static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        return (candidates, candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index));
    }
}