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
                options),
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
                options),
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

        var gammaLength = Math.Max(1, n);
        var gammaGeneral = new long[gammaLength];
        for (var k = 0; k < gammaLength; k++)
        {
            gammaGeneral[k] = WeightedObjectiveInputs.Scale;
        }

        var gammaLoved = new long[gammaLength];
        for (var k = 0; k < gammaLength; k++)
        {
            gammaLoved[k] = (long)Math.Round(Math.Pow(p.GammaLoved, k) * WeightedObjectiveInputs.Scale);
        }

        var gammaDefault = new long[gammaLength];
        for (var k = 0; k < gammaLength; k++)
        {
            gammaDefault[k] = (long)Math.Round(Math.Pow(p.GammaDefault, k) * WeightedObjectiveInputs.Scale);
        }

        var groupMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var diversityGroupIndex = new int[n];
        var gammaScaledByNode = new long[n][];

        for (var i = 0; i < n; i++)
        {
            var rawCategory = ctx.Nodes[i].Candidate.CategoryName;
            var groupName = PoiCategoryNames.GetDiversityGroup(rawCategory);
            if (!groupMap.TryGetValue(groupName, out var groupIndex))
            {
                groupIndex = groupMap.Count;
                groupMap[groupName] = groupIndex;
            }
            diversityGroupIndex[i] = groupIndex;

            var canonicalCat = PoiCategoryNames.NormalizeToCanonical(rawCategory);
            if (string.Equals(canonicalCat, PoiCategoryNames.General, StringComparison.OrdinalIgnoreCase))
            {
                gammaScaledByNode[i] = gammaGeneral;
            }
            else if (p.LovedCategories.Contains(canonicalCat))
            {
                gammaScaledByNode[i] = gammaLoved;
            }
            else
            {
                gammaScaledByNode[i] = gammaDefault;
            }
        }

        var groupCount = Math.Max(1, groupMap.Count);

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
            DiversityGroupIndex = diversityGroupIndex,
            DiversityGroupCount = groupCount,
            GammaScaledByNode = gammaScaledByNode,
            IsExposedOutdoor = isExposedOutdoor,
            IsFood = isFood,
            IsRestStop = isRestStop,
            Effort = effort,
            TimeFit = timeFit,
            P = p
        };
    }
}