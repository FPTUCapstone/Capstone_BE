using FluentAssertions;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

/// <summary>
/// Bản sao nguyên văn của EliteSet cũ (trước C2) dùng làm Oracle kiểm chứng đối chứng
/// cho EliteSet mới và LexEliteOrdering.
/// </summary>
internal sealed class LegacyEliteSetOracle(CspProductRule rule, int capacity)
{
    private readonly List<(RouteState State, ulong[] Inclusion)> _items = [];

    public RouteState? Best => _items.Count == 0 ? null : _items[0].State;

    public IEnumerable<RouteState> Ordered => _items.Select(item => item.State);

    public bool MayAccept(ulong[] inclusion, int travel, int finish)
    {
        if (_items.Count < capacity)
        {
            return true;
        }

        var (worst, worstInclusion) = _items[^1];
        var comparison = CspProductRule.CompareInclusion(inclusion, worstInclusion);
        if (comparison != 0)
        {
            return comparison < 0;
        }

        comparison = travel.CompareTo(worst.TravelMinutes);
        return comparison != 0 ? comparison < 0 : finish <= worst.Finish;
    }

    public void Offer(RouteState state) => Offer(state, rule.Inclusion(state.Sequence));

    public void Offer(RouteState state, ulong[] inclusion)
    {
        var index = 0;
        while (index < _items.Count)
        {
            var comparison = rule.Compare(state, inclusion, _items[index].State, _items[index].Inclusion);
            if (comparison == 0)
            {
                // Product rule so tới cả dãy Id, nên bằng nhau nghĩa là cùng một lộ trình.
                return;
            }

            if (comparison < 0)
            {
                break;
            }

            index++;
        }

        if (index >= capacity)
        {
            return;
        }

        _items.Insert(index, (state, inclusion));
        if (_items.Count > capacity)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }
}

public class EliteSetOracleTests
{
    private static (RoutingContext Ctx, CspProductRule Rule, List<RouteState> Pool) BuildRoutePool(int size)
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(25, 2, seed: 100, RestPreference.None);
        var candidates = input.Candidates.ToArray();
        var indices = candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var options = new SchedulingGenerationOptions();
        var ctx = CspItinerarySolver.CreatePrimaryContext(input, matrix, candidates, indices, options);
        var rule = CspProductRule.Create(ctx, candidates, indices, matrix);

        var pool = new List<RouteState>();
        var rand = new Random(12345);

        // Sinh các lộ trình đơn lẻ và kết hợp ngẫu nhiên
        for (var iter = 0; iter < 100_000 && pool.Count < size; iter++)
        {
            var len = rand.Next(1, 6);
            var seq = new List<int>();
            while (seq.Count < len)
            {
                var node = rand.Next(ctx.Nodes.Count);
                if (!seq.Contains(node))
                {
                    seq.Add(node);
                }
            }

            if (RouteState.Build(ctx, seq) is { } state)
            {
                pool.Add(state);
            }
        }

        if (pool.Count == 0)
        {
            throw new InvalidOperationException("Không sinh được RouteState nào trong pool.");
        }

        return (ctx, rule, pool);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public void New_EliteSet_matches_legacy_on_random_offer_sequences(int capacity)
    {
        var rng = new Random(20250101 + capacity);
        var (ctx, rule, pool) = BuildRoutePool(size: 100);

        var legacy = new LegacyEliteSetOracle(rule, capacity);
        var actual = new EliteSet(new LexEliteOrdering(rule), capacity);

        for (var step = 0; step < 10_000; step++)
        {
            var state = pool[rng.Next(pool.Count)];
            var inclusion = rule.Inclusion(state.Sequence);
            var key = new EliteKey(inclusion, state.TravelMinutes, state.Finish);

            var legacyMayAccept = legacy.MayAccept(inclusion, state.TravelMinutes, state.Finish);
            var actualMayAccept = actual.MayAccept(key);
            actualMayAccept.Should().Be(legacyMayAccept, $"Bước {step}: MayAccept phải cho kết quả giống hệt nhau");

            if (rng.Next(2) == 0)
            {
                // Đường đi GLS Polish: chỉ truyền RouteState, EliteSet tự gọi CreateKey
                legacy.Offer(state);
                actual.Offer(state);
            }
            else
            {
                // Đường đi trong SearchRun.RecordSolution: truyền cả RouteState và key
                var clonedInclusion = (ulong[])inclusion.Clone();
                legacy.Offer(state, clonedInclusion);
                actual.Offer(state, new EliteKey(clonedInclusion, state.TravelMinutes, state.Finish));
            }

            var legacyOrdered = legacy.Ordered.Select(s => (s.Key, s.TravelMinutes, s.Finish)).ToList();
            var actualOrdered = actual.Ordered.Select(s => (s.Key, s.TravelMinutes, s.Finish)).ToList();
            actualOrdered.Should().Equal(legacyOrdered, $"Bước {step}: Thứ tự các phần tử trong tập ưu tú phải trùng khớp 100%");
        }
    }
}
