using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Constants;


namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Factory khởi tạo kernel hàm mục tiêu dựa trên tùy chọn CSP.
/// </summary>
internal static class ObjectiveKernelFactory
{
    public static IObjectiveKernel Create(
        CspProductRule rule,
        CspOptions options,
        WeightedObjectiveInputs? weightedInputs = null)
    {
        return options.Mode switch
        {
            ObjectiveMode.WeightedUtility => new WeightedUtilityKernel(
                weightedInputs ?? throw new InvalidOperationException(
                    "WeightedObjectiveInputs must be provided when ObjectiveMode is WeightedUtility."),
                options,
                options.DisableBound,
                options.BoundVariant),
            _ => new LexicographicKernel(rule, options),
        };
    }

    public static IObjectiveKernel Create(
        RoutingContext ctx,
        CspProductRule rule,
        CspOptions options,
        WeightedRequestInputs? weighted = null)
    {
        return options.Mode switch
        {
            ObjectiveMode.WeightedUtility => new WeightedUtilityKernel(
                BuildWeightedInputs(ctx, weighted ?? throw new InvalidOperationException(
                    "WeightedRequestInputs must be provided when ObjectiveMode is WeightedUtility.")),
                options,
                options.DisableBound,
                options.BoundVariant),
            _ => new LexicographicKernel(rule, options),
        };
    }

    /// <summary>
    /// Dựng mảng điểm Base theo thứ tự node index của RoutingContext từ bảng tra BaseByPoiId.
    /// Nếu một POI hợp lệ trong context không có trong bảng tra, ném InvalidOperationException.
    /// </summary>
    public static double[] BuildBaseScores(RoutingContext ctx, IReadOnlyDictionary<long, double> baseByPoiId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(baseByPoiId);

        var n = ctx.Nodes.Count;
        var baseScores = new double[n];
        for (var v = 0; v < n; v++)
        {
            var poiId = ctx.Nodes[v].Candidate.Id;
            if (!baseByPoiId.TryGetValue(poiId, out var baseVal))
            {
                throw new InvalidOperationException($"POI ID {poiId} is missing in BaseByPoiId.");
            }
            baseScores[v] = baseVal;
        }

        return baseScores;
    }

    /// <summary>
    /// Dựng đầy đủ cấu trúc WeightedObjectiveInputs cho một RoutingContext từ WeightedRequestInputs.
    /// </summary>
    public static WeightedObjectiveInputs BuildWeightedInputs(
        RoutingContext ctx,
        WeightedRequestInputs weighted)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(weighted);

        var n = ctx.Nodes.Count;
        var baseScores = BuildBaseScores(ctx, weighted.BaseByPoiId);

        var p = weighted.Params;
        var timeFit = weighted.TimeFit.Create(ctx);

        var categoryMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryIndex = new int[n];
        for (var i = 0; i < n; i++)
        {
            var catName = PoiCategoryNames.NormalizeToCanonical(ctx.Nodes[i].Candidate.CategoryName);
            if (!categoryMap.TryGetValue(catName, out var idx))
            {
                idx = categoryMap.Count;
                categoryMap[catName] = idx;
            }
            categoryIndex[i] = idx;
        }

        var catCount = Math.Max(1, categoryMap.Count);
        var gammaScaled = new long[catCount][];
        foreach (var (catName, c) in categoryMap)
        {
            gammaScaled[c] = new long[32];
            var factor = p.LovedCategories.Contains(catName) ? p.GammaLoved : p.GammaDefault;
            for (var k = 0; k < 32; k++)
            {
                gammaScaled[c][k] = (long)Math.Round(Math.Pow(factor, k) * WeightedObjectiveInputs.Scale);
            }
        }

        if (categoryMap.Count == 0)
        {
            gammaScaled[0] = new long[32];
            for (var k = 0; k < 32; k++)
            {
                gammaScaled[0][k] = (long)Math.Round(Math.Pow(p.GammaDefault, k) * WeightedObjectiveInputs.Scale);
            }
        }

        var isExposedOutdoor = new bool[n];
        var isFood = new bool[n];
        var isRestStop = new bool[n];
        var effort = new int[n];

        for (var i = 0; i < n; i++)
        {
            var cand = ctx.Nodes[i].Candidate;
            isExposedOutdoor[i] = !cand.HasShelter;
            isFood[i] = string.Equals(cand.CategoryName, PoiCategoryNames.FoodAndDrink, StringComparison.OrdinalIgnoreCase);
            isRestStop[i] = ItineraryScheduleEvaluator.IsQualifiedRestCandidate(cand);
            effort[i] = cand.VisitDurationMinutes;
        }

        return new WeightedObjectiveInputs
        {
            Base = baseScores,
            CategoryIndex = categoryIndex,
            CategoryCount = catCount,
            GammaScaled = gammaScaled,
            IsExposedOutdoor = isExposedOutdoor,
            IsFood = isFood,
            IsRestStop = isRestStop,
            Effort = effort,
            TimeFit = timeFit,
            P = p
        };
    }
}
