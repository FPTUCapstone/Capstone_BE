using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Csp;

public class CspItinerarySolverTests
{
    private static readonly SchedulingGenerationOptions Options = new();

    private static readonly CspOptions Exhaustive = new()
    {
        MaxStops = 99,
        MaxOptionalDomainSize = 99,
        MaxNodes = 10_000_000,
        TimeLimitMilliseconds = 60_000,
    };

    /// <summary>
    /// Forward checking và branch and bound không được bỏ sót lời giải: trên bài nhỏ, CSP phải cho đúng
    /// mục tiêu tối ưu của phép duyệt vét cạn không cắt tỉa, hoặc cùng kết luận vô nghiệm.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Solve_MatchesExhaustiveSearch_OnSmallInstances(int seed)
    {
        var (input, matrix) = SmallScenario(seed);
        var (candidates, indices) = Index(input);
        var ctx = RoutingContext.Create(input, matrix, candidates, indices, Options, Options.MiniRouting);
        var exhaustive = ExhaustiveBest(ctx);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options, Exhaustive)
            .Solve(input, matrix, candidates, indices);

        result.Statistics.SearchCompleted.Should().BeTrue();
        if (exhaustive is null)
        {
            result.ProvedInfeasible.Should().BeTrue();
        }
        else
        {
            result.Schedule.Should().NotBeNull();
            result.Statistics.BestObjective.Should().Be(exhaustive.Value);
        }
    }

    [Theory]
    [InlineData(20, 2, 41)]
    [InlineData(40, 4, 42)]
    public void Solve_ReturnsValidScheduleWithAllMandatoryPois(int candidateCount, int mandatoryCount, int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(candidateCount, mandatoryCount, seed);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Schedule!.VisitPoiIds.Should().Contain(input.MandatoryPoiIds);
        var validate = () => new GeneratedItineraryInvariantValidator(Options).Validate(input, matrix, candidates, result.Schedule.Plan);
        validate.Should().NotThrow();
    }

    [Fact]
    public void Solve_ProvesOptimality_AndPrunes_WhenOpeningHoursAreTight()
    {
        var (input, matrix) = TightScenario(5);
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.Schedule.Should().NotBeNull();
        result.Statistics.SearchCompleted.Should().BeTrue();
        (result.Statistics.PrunedByForwardChecking + result.Statistics.PrunedByBound).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Solve_ProvesInfeasibility_AndNamesTheConflictingMandatoryPoi()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 51);
        var closedId = input.MandatoryPoiIds.First();
        input = input with
        {
            Candidates = input.Candidates.Select(c => c.Id == closedId ? c with { OpeningHours = [] } : c).ToArray(),
        };
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.ProvedInfeasible.Should().BeTrue();
        result.ConflictingMandatoryPoiIds.Should().Contain(closedId);
    }

    [Fact]
    public void Solve_ProvesInfeasibility_WhenTwoMandatoryPoisAreOpenOnlyAtTheSameHour()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(8, 2, 52);
        var oneHour = new GenerationOpeningHours(2, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var mandatory = input.MandatoryPoiIds.ToHashSet();
        input = input with
        {
            Candidates = input.Candidates
                .Select(c => mandatory.Contains(c.Id) ? c with { VisitDurationMinutes = 45, OpeningHours = [oneHour] } : c)
                .ToArray(),
        };
        var (candidates, indices) = Index(input);

        var result = new CspItinerarySolver(new ItineraryScheduleEvaluator(Options), Options).Solve(input, matrix, candidates, indices);

        result.ProvedInfeasible.Should().BeTrue();
        result.ConflictingMandatoryPoiIds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerationService_ReturnsConflictingPoiName_WhenCspProvesInfeasibility()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 53);
        var closed = input.Candidates.First(c => input.MandatoryPoiIds.Contains(c.Id));
        input = input with
        {
            Candidates = input.Candidates.Select(c => c.Id == closed.Id ? c with { OpeningHours = [] } : c).ToArray(),
        };
        var options = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        result.ErrorMessage.Should().Contain(closed.Name);
    }

    [Fact]
    public async Task GenerationService_UsesCsp_WhenSolverModeIsCsp()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(20, 2, 54);
        var options = new SchedulingGenerationOptions { SolverMode = SchedulingSolverMode.Csp };
        var service = new ItineraryGenerationService(new StaticMatrixProvider(matrix), options);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.IsMandatory)
            .Select(item => item.PointOfInterestId!.Value)
            .Should().BeEquivalentTo(input.MandatoryPoiIds);
    }

    private static long? ExhaustiveBest(RoutingContext ctx)
    {
        long? best = null;
        var sequence = new List<int>();

        void Visit()
        {
            var state = RouteState.Build(ctx, sequence);
            if (state is null)
            {
                return;
            }

            if (state.Count > 0 && state.HasAllMandatory && (best is null || state.Objective < best))
            {
                best = state.Objective;
            }

            for (var node = 0; node < ctx.Nodes.Count; node++)
            {
                if (!sequence.Contains(node))
                {
                    sequence.Add(node);
                    Visit();
                    sequence.RemoveAt(sequence.Count - 1);
                }
            }
        }

        Visit();
        return best;
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) SmallScenario(int seed)
    {
        var random = new Random(seed);
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(6 + (seed % 3), seed % 4, seed);
        var tight = seed % 2 == 0;
        return (input with
        {
            AvailableMinutes = random.Next(2, 7) * 60,
            BudgetVnd = random.Next(3, 9) * 15_000m,
            Candidates = input.Candidates
                .Select(c => !tight
                    ? c
                    : c with
                    {
                        OpeningHours =
                        [
                            new GenerationOpeningHours(2, new TimeOnly(random.Next(8, 12), 0), new TimeOnly(random.Next(10, 15), 0)),
                        ],
                    })
                .ToArray(),
        }, matrix);
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) TightScenario(int seed)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(30, 3, seed);
        var random = new Random(seed * 97);
        var mandatory = input.MandatoryPoiIds.ToHashSet();
        return (input with
        {
            Candidates = input.Candidates
                .Select(c =>
                {
                    if (mandatory.Contains(c.Id))
                    {
                        return c;
                    }

                    var open = random.Next(8, 15);
                    var close = Math.Min(22, open + random.Next(1, 4));
                    return c with { OpeningHours = [new GenerationOpeningHours(2, new TimeOnly(open, 0), new TimeOnly(close, 0))] };
                })
                .ToArray(),
        }, matrix);
    }

    private static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        return (candidates, candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index));
    }

    private sealed class StaticMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}