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
        long totalGap = 0;

        for (var seed = 1; seed <= InstancesPerPreference; seed++)
        {
            var (input, matrix) = RandomInstance(seed, restPreference);
            var (candidates, indices) = Index(input);
            var penalties = Penalties(input, matrix, candidates, indices);
            var optimum = EvaluatorOptimum(evaluator, input, matrix, candidates, indices, penalties);

            var result = new CspItinerarySolver(evaluator, Options, Exhaustive).Solve(input, matrix, candidates, indices);

            if (result.Schedule is not null)
            {
                var objective = EvaluatedObjective(result.Schedule, input, penalties);
                result.Statistics.BestEvaluatedObjective.Should().Be(objective, "the solver reports the objective of the schedule it returns");
                optimum.Should().NotBeNull("the solver returned a valid schedule, so the exhaustive search finds one too");
                objective.Should().BeGreaterThanOrEqualTo(optimum!.Value, "no schedule beats the exhaustive optimum");
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
                continue;
            }

            var gap = EvaluatedObjective(result.Schedule, input, penalties) - optimum.Value;
            if (gap == 0)
            {
                optimal++;
            }
            else
            {
                worse++;
                totalGap += gap;
            }
        }

        output.WriteLine($"{restPreference}: {feasible} feasible, {optimal} optimal, {worse} worse (total gap {totalGap}), {missed} without a CSP schedule");

        // Đo trên chính các instance này: khi chỉ chạy mô hình nới lỏng lúc mô hình có dự phòng không ra lời giải
        // (head 94995d9), CSP trả lịch tệ hơn tối ưu ở 6/77 instance Auto (tổng chênh 243) và 23/69 instance Frequent
        // (tổng chênh 1746). Chạy cả hai mô hình và chọn qua evaluator còn 0 và 1 (chênh 81). Không được tệ hơn mức này.
        feasible.Should().BeGreaterThan(InstancesPerPreference / 2, "the generator must produce mostly feasible instances");
        missed.Should().Be(0);
        worse.Should().BeLessThanOrEqualTo(restPreference == RestPreference.Auto ? 0 : 1);
    }

    /// <summary>
    /// Khi không có điểm nghỉ thì mục tiêu CSP chính là mục tiêu trên lịch thật: CSP phải tối ưu.
    /// </summary>
    [Fact]
    public void Solve_MatchesTheEvaluatorOptimum_WithoutRest()
    {
        var evaluator = new ItineraryScheduleEvaluator(Options);
        for (var seed = 1; seed <= InstancesPerPreference / 2; seed++)
        {
            var (input, matrix) = RandomInstance(seed, RestPreference.None);
            var (candidates, indices) = Index(input);
            var penalties = Penalties(input, matrix, candidates, indices);
            var optimum = EvaluatorOptimum(evaluator, input, matrix, candidates, indices, penalties);

            var result = new CspItinerarySolver(evaluator, Options, Exhaustive).Solve(input, matrix, candidates, indices);

            if (optimum is null)
            {
                result.Schedule.Should().BeNull();
                result.ProvedInfeasible.Should().BeTrue();
            }
            else
            {
                result.Schedule.Should().NotBeNull();
                EvaluatedObjective(result.Schedule!, input, penalties).Should().Be(optimum.Value);
            }
        }
    }

    /// <summary>
    /// Hai lần chạy (có và không dự phòng nghỉ) dùng chung một giới hạn số nút: tổng số nút không vượt MaxNodes + 2
    /// (mỗi lần chạy dừng ở nút đầu tiên vượt giới hạn).
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
        result.Statistics.NodesExpanded.Should().BeLessThanOrEqualTo(limits.MaxNodes + 2);
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

    /// <summary>Mục tiêu tốt nhất trên mọi thứ tự điểm đến chứa đủ điểm bắt buộc mà evaluator chấp nhận.</summary>
    private static long? EvaluatorOptimum(
        ItineraryScheduleEvaluator evaluator,
        GenerationInput input,
        RouteDurationMatrix matrix,
        GenerationCandidate[] candidates,
        Dictionary<long, int> indices,
        Dictionary<long, int> penalties)
    {
        long? best = null;
        var sequence = new List<GenerationCandidate>();
        var used = new bool[candidates.Length];

        void Visit()
        {
            if (sequence.Count > 0 && input.MandatoryPoiIds.All(id => sequence.Any(c => c.Id == id)))
            {
                var schedule = evaluator.Evaluate(input, sequence.ToArray(), matrix, candidates, indices, CancellationToken.None);
                if (schedule is not null)
                {
                    var objective = EvaluatedObjective(schedule, input, penalties);
                    best = best is null ? objective : Math.Min(best.Value, objective);
                }
            }

            for (var i = 0; i < candidates.Length; i++)
            {
                if (!used[i] && penalties.ContainsKey(candidates[i].Id))
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

    private static long EvaluatedObjective(
        EvaluatedItinerarySchedule schedule,
        GenerationInput input,
        Dictionary<long, int> penalties) =>
        schedule.TotalMatrixTravelMinutes
        + penalties.Where(pair => !input.MandatoryPoiIds.Contains(pair.Key) && !schedule.VisitPoiIds.Contains(pair.Key))
            .Sum(pair => (long)pair.Value);

    /// <summary>Mức phạt bỏ điểm của mọi POI mà bộ giải có thể xếp (điểm bắt buộc có phạt 0).</summary>
    private static Dictionary<long, int> Penalties(
        GenerationInput input,
        RouteDurationMatrix matrix,
        GenerationCandidate[] candidates,
        Dictionary<long, int> indices)
    {
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
        return ctx.Nodes.ToDictionary(node => node.Candidate.Id, node => node.SkipPenalty);
    }

    private static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        return (candidates, candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index));
    }
}