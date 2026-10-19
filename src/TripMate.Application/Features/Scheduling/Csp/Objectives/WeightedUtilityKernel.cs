using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Đại diện incumbent nội bộ của WeightedUtilityKernel trong một RoutingContext cụ thể.
/// </summary>
internal sealed class WeightedIncumbent(long objective, int travel, int finish, long[] visitPoiIds)
{
    public long Objective { get; } = objective;
    public int Travel { get; } = travel;
    public int Finish { get; } = finish;
    public long[] VisitPoiIds { get; } = visitPoiIds;

    public static WeightedIncumbent From(long objective, int travel, int finish, int[] sequence, int depth, RoutingContext ctx)
    {
        var ids = new long[depth];
        for (var i = 0; i < depth; i++)
        {
            ids[i] = ctx.Nodes[sequence[i]].Candidate.Id;
        }
        return new WeightedIncumbent(objective, travel, finish, ids);
    }
}

/// <summary>
/// Triển khai IObjectiveKernel cho bài toán CSP theo hàm mục tiêu cá nhân hóa (Personalized Weighted Utility).
/// Tích lũy điểm mục tiêu và trạng thái phạt mềm qua mảng bộ đệm theo độ sâu (Zero Heap Allocation during Search).
public enum BoundVariant
{
    Correct,
    OmitMandatoryGain,
    GreedySlotsOnly,
    ApplyGamma,
    NoRoundingSlack,
    IgnoreOptionalIncomingTravel,
    StaticMaxFactor,
    PerCallSuffix
}

/// <summary>
/// Triển khai IObjectiveKernel cho bài toán CSP theo hàm mục tiêu cá nhân hóa (Personalized Weighted Utility).
/// Tích lũy điểm mục tiêu và trạng thái phạt mềm qua mảng bộ đệm theo độ sâu (Zero Heap Allocation during Search).
/// Tuân thủ quy tắc kiến trúc:
/// 1. Incumbent luôn so sánh bằng J mô hình (ModelObjective). Evaluator chỉ xác nhận tính khả thi khi có điểm nghỉ.
/// 2. Token luân chuyển giữa các run chỉ mang dãy POI ID và luôn được phát lại (Replay) trong context nhận.
/// 3. So sánh incumbent đồng bộ đủ 4 cấp (Objective -> Travel -> Finish -> POI IDs) với WeightedEliteOrdering.
/// </summary>
internal sealed class WeightedUtilityKernel : IObjectiveKernel
{
    private readonly WeightedObjectiveInputs _inputs;
    private readonly CspOptions _options;
    private readonly bool _disableBound;
    private readonly BoundVariant _variant;

    private RoutingContext _ctx = null!;
    private SearchBuffers _buffers = null!;
    private SearchPrecomputed _pre = null!;
    private CspStatistics _stats = null!;

    private Dictionary<long, int> _nodeOf = null!;

    private long[] _obj = null!;
    private SoftState[] _soft = null!;
    private int[] _categoryCount = null!;

    private int[] _catScratch = null!;
    private List<GeneratedItineraryItem> _itemScratch = null!;

    private long[] _uMax = null!;
    private long[] _vEff = null!;
    private int[] _weight = null!;
    private int[] _byDensity = null!;
    private int[] _byValue = null!;
    private int[] _mark = null!;
    private int _stamp;
    private int _maxStops;

    private int[] _candNodes = null!;
    private long[] _candVal = null!;
    private double[] _candDensity = null!;
    private int[] _slotsOrder = null!;
    private int[] _timeOrder = null!;

    private const int BucketMinutes = 15;
    private int _bucketCount;
    private long[] _uMaxMandatoryB = null!;
    private long[] _vEffB = null!;
    private int[] _byDensityB = null!;
    private int[] _byValueB = null!;

    private WeightedIncumbent? _incumbent;
    private long _pendingObjective;
    private int _pendingTravel;
    private int _pendingFinish;
    private int _pendingDepth;
    private int _pendingComparison;

    private WeightedEliteOrdering _eliteOrdering = null!;
    private WeightedScheduleComparer _scheduleComparer = null!;

    public WeightedUtilityKernel(
        WeightedObjectiveInputs inputs,
        CspOptions options) : this(inputs, options, false, BoundVariant.Correct)
    {
    }

    internal WeightedUtilityKernel(
        WeightedObjectiveInputs inputs,
        CspOptions options,
        bool disableBound = false,
        BoundVariant variant = BoundVariant.Correct)
    {
        _inputs = inputs;
        _options = options;
        _disableBound = disableBound;
        _variant = variant;
    }

    public IEliteOrdering EliteOrdering => _eliteOrdering;

    public IComparer<EvaluatedItinerarySchedule> ScheduleComparer => _scheduleComparer;

    public bool HasIncumbent => _incumbent is not null;

    internal long? IncumbentObjectiveForTest => _incumbent?.Objective;
    internal WeightedIncumbent? IncumbentForTest => _incumbent;
    internal long[] UMaxForTest => _uMax;
    internal long[] VEffForTest => _vEff;
    internal int[] WeightForTest => _weight;
    internal int[] ByDensityForTest => _byDensity;
    internal int[] ByValueForTest => _byValue;
    internal BoundVariant VariantForTest => _variant;
    internal bool DisableBoundForTest => _disableBound;

    internal void SetIncumbentForTest(long objective, int travel = 0, int finish = 0, long[]? visitPoiIds = null)
    {
        _incumbent = new WeightedIncumbent(objective, travel, finish, visitPoiIds ?? []);
    }

    public void Initialize(RoutingContext ctx, SearchBuffers buffers, SearchPrecomputed pre, CspStatistics statistics)
    {
        _ctx = ctx;
        _buffers = buffers;
        _pre = pre;
        _stats = statistics;

        for (var v = 0; v < _inputs.Base.Length; v++)
        {
            var b = _inputs.Base[v];
            Invariant.Check(b is >= 0.0 and <= 1.0, $"Base[{v}] = {b} is outside [0, 1].");
        }

        var maxDepth = _buffers.Sequence.Length;

        _obj = new long[maxDepth + 1];
        _soft = new SoftState[maxDepth + 1];
        _categoryCount = new int[_inputs.CategoryCount];
        _catScratch = new int[_inputs.CategoryCount];
        _itemScratch = new List<GeneratedItineraryItem>(maxDepth + 4);

        _nodeOf = new Dictionary<long, int>(_ctx.Nodes.Count);
        for (var node = 0; node < _ctx.Nodes.Count; node++)
        {
            var id = _ctx.Nodes[node].Candidate.Id;
            _nodeOf[id] = node;
        }

        _eliteOrdering = new WeightedEliteOrdering(_inputs, _ctx, _catScratch);
        _scheduleComparer = new WeightedScheduleComparer(_inputs, _ctx, _nodeOf, _ctx.CandidateMatrixIndices, _catScratch, _itemScratch);

        _maxStops = _options.MaxStops ?? int.MaxValue;
        var n = _ctx.Nodes.Count;

        _candNodes = new int[n];
        _candVal = new long[n];
        _candDensity = new double[n];
        _slotsOrder = new int[n];
        _timeOrder = new int[n];

        _uMax = new long[n];
        _vEff = new long[n];
        _weight = new int[n];
        for (var v = 0; v < n; v++)
        {
            // γ^0 = 1 là lớn nhất; MaxFactor là TimeFit lạc quan nhất. Ceiling để không bao giờ nhỏ hơn gain thật.
            var maxFactor = _inputs.TimeFit.MaxFactor(v);
            if (_variant == BoundVariant.ApplyGamma)
            {
                maxFactor *= _inputs.P.GammaLoved;
            }

            _uMax[v] = (long)Math.Ceiling(WeightedObjectiveInputs.Scale * _inputs.Base[v] * maxFactor);
            _weight[v] = _ctx.Nodes[v].VisitMinutes + _pre.MinIncomingTravel[v];

            if (_variant == BoundVariant.IgnoreOptionalIncomingTravel)
            {
                _vEff[v] = _uMax[v];
            }
            else
            {
                var minIncomingCost = (long)Math.Floor(WeightedObjectiveInputs.Scale * _inputs.P.LambdaPerMin * _pre.MinIncomingRaw[v]);
                _vEff[v] = _uMax[v] - minIncomingCost;
            }
        }

        _byDensity = Enumerable.Range(0, n)
            .OrderByDescending(v => (double)_vEff[v] / Math.Max(1, _weight[v]))
            .ThenBy(v => _ctx.Nodes[v].Candidate.Id)
            .ToArray();

        _byValue = Enumerable.Range(0, n)
            .OrderByDescending(v => _vEff[v])
            .ThenBy(v => _ctx.Nodes[v].Candidate.Id)
            .ToArray();

        _mark = new int[n];
        _stamp = 0;

        _bucketCount = (_ctx.Horizon / BucketMinutes) + 1;
        var totalSlots = _bucketCount * n;

        _uMaxMandatoryB = new long[totalSlots];
        _vEffB = new long[totalSlots];
        _byDensityB = new int[totalSlots];
        _byValueB = new int[totalSlots];

        var tempIndices = new int[n];

        for (var b = 0; b < _bucketCount; b++)
        {
            var tB = Math.Min(b * BucketMinutes, _ctx.Horizon);
            var offset = b * n;

            for (var v = 0; v < n; v++)
            {
                var factor = _inputs.TimeFit.MaxFactorFrom(v, tB);
                if (_variant == BoundVariant.ApplyGamma)
                {
                    factor *= _inputs.P.GammaLoved;
                }

                var uMax = (long)Math.Ceiling(WeightedObjectiveInputs.Scale * _inputs.Base[v] * factor);
                _uMaxMandatoryB[offset + v] = uMax;

                if (_variant == BoundVariant.IgnoreOptionalIncomingTravel)
                {
                    _vEffB[offset + v] = uMax;
                }
                else
                {
                    var minIncomingCost = (long)Math.Floor(WeightedObjectiveInputs.Scale * _inputs.P.LambdaPerMin * _pre.MinIncomingRaw[v]);
                    _vEffB[offset + v] = uMax - minIncomingCost;
                }
            }

            for (var v = 0; v < n; v++) tempIndices[v] = v;
            Array.Sort(tempIndices, (a, bNode) =>
            {
                var da = (double)_vEffB[offset + a] / Math.Max(1, _weight[a]);
                var db = (double)_vEffB[offset + bNode] / Math.Max(1, _weight[bNode]);
                var cmp = db.CompareTo(da);
                return cmp != 0 ? cmp : _ctx.Nodes[a].Candidate.Id.CompareTo(_ctx.Nodes[bNode].Candidate.Id);
            });
            Array.Copy(tempIndices, 0, _byDensityB, offset, n);

            for (var v = 0; v < n; v++) tempIndices[v] = v;
            Array.Sort(tempIndices, (a, bNode) =>
            {
                var va = _vEffB[offset + a];
                var vb = _vEffB[offset + bNode];
                var cmp = vb.CompareTo(va);
                return cmp != 0 ? cmp : _ctx.Nodes[a].Candidate.Id.CompareTo(_ctx.Nodes[bNode].Candidate.Id);
            });
            Array.Copy(tempIndices, 0, _byValueB, offset, n);
        }
    }

    public IncumbentToken? AcceptInitial(EvaluatedItinerarySchedule? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        var maxStops = _options.MaxStops ?? int.MaxValue;
        if (schedule.VisitPoiIds.Count > maxStops
            || schedule.VisitPoiIds.Any(id => !_nodeOf.TryGetValue(id, out var node) || !_pre.InInitialDomain[node]))
        {
            return null;
        }

        _stats.InitialIncumbentAccepted = true;
        return new WeightedIncumbentToken(schedule.VisitPoiIds.ToArray());
    }

    public void ImportIncumbent(IncumbentToken? token)
    {
        _incumbent = null;
        if (token is null)
        {
            return;
        }

        if (token is not WeightedIncumbentToken w)
        {
            throw new InvalidOperationException($"Không nhập được {token.GetType().Name} vào WeightedUtilityKernel.");
        }

        if (!TryMapToNodes(w.VisitPoiIds, out var seq))
        {
            return;
        }

        if (RouteState.Build(_ctx, seq) is not { } route)
        {
            return;
        }

        // Luôn phát lại (Replay), phản ánh mọi khác biệt giữa hai context (kể cả thời gian di chuyển, buffer, v.v.)
        var j = ObjectiveCalculator.Replay(_inputs, _ctx, route.Sequence, route.Earliest, _catScratch);
        _incumbent = new WeightedIncumbent(j, route.TravelMinutes, route.Finish, w.VisitPoiIds);
    }

    public IncumbentToken? ExportIncumbent() =>
        _incumbent is null ? null : new WeightedIncumbentToken(_incumbent.VisitPoiIds);

    public void OnPush(int depth, int value, int start, int fromMatrix)
    {
        var node = _ctx.Nodes[value];
        var travelRaw = _ctx.RawTravel(fromMatrix, node.MatrixIndex);
        var prevDeparture = depth == 0 ? 0 : _buffers.Departure[depth - 1];
        var arrive = prevDeparture + _ctx.Travel(fromMatrix, node.MatrixIndex);
        var wait = start - arrive;
        var end = start + node.VisitMinutes;
        var cat = _inputs.CategoryIndex[value];
        var catCountBefore = _categoryCount[cat];

        ref var soft = ref _soft[depth + 1];
        soft = _soft[depth];

        var delta = ObjectiveCalculator.VisitTerm(_inputs, value, start, end, travelRaw, wait, catCountBefore, ref soft);
        _obj[depth + 1] = _obj[depth] + delta;
        _categoryCount[cat]++;
    }

    public void OnPop(int depth, int value)
    {
        var cat = _inputs.CategoryIndex[value];
        _categoryCount[cat]--;
    }

    public bool CanImprove(int[] domain, int domainCount, int depth, int departure, int current)
    {
        if (_disableBound || _incumbent is null) return true;

        var timeLeft = _ctx.Horizon - departure - _pre.MinReturnTravel;
        var budgetLeft = _ctx.Budget - _buffers.Cost[depth];
        long mandatoryGain = 0, forcedTravel = 0;
        var remainingMandatory = 0;

        var bIdx = Math.Min(departure / BucketMinutes, _bucketCount - 1);
        var bOffset = bIdx * _ctx.Nodes.Count;

        foreach (var m in _ctx.MandatoryNodes)
        {
            if (_buffers.Contains[m]) continue;
            remainingMandatory++;
            if (_variant != BoundVariant.OmitMandatoryGain)
            {
                if (_variant == BoundVariant.StaticMaxFactor || _variant == BoundVariant.IgnoreOptionalIncomingTravel)
                {
                    mandatoryGain += _uMax[m];
                }
                else if (_variant == BoundVariant.PerCallSuffix)
                {
                    var factor = _inputs.TimeFit.MaxFactorFrom(m, departure);
                    if (_variant == BoundVariant.ApplyGamma)
                    {
                        factor *= _inputs.P.GammaLoved;
                    }
                    mandatoryGain += (long)Math.Ceiling(WeightedObjectiveInputs.Scale * _inputs.Base[m] * factor);
                }
                else
                {
                    mandatoryGain += _uMaxMandatoryB[bOffset + m];
                }
            }
            forcedTravel += _pre.MinIncomingRaw[m];
            timeLeft -= _pre.MinIncomingTravel[m] + _ctx.Nodes[m].VisitMinutes;
            budgetLeft -= _ctx.Nodes[m].Cost;
        }

        var finalReturn = _ctx.RawTravel(current, _ctx.EndMatrixIndex);
        var maxVisits = Math.Min(_maxStops, _ctx.Nodes.Count);
        var slots = Math.Max(0, maxVisits - depth - remainingMandatory);

        long ubTime = 0;
        long ubSlots = 0;

        if (_variant == BoundVariant.StaticMaxFactor || _variant == BoundVariant.IgnoreOptionalIncomingTravel)
        {
            _stamp++;
            for (var i = 0; i < domainCount; i++)
            {
                var v = domain[i];
                finalReturn = Math.Min(finalReturn, _pre.ReturnRaw[v]);
                var node = _ctx.Nodes[v];
                if (node.IsMandatory) continue;
                if (budgetLeft is { } left && node.Cost > left) continue;
                if (_variant != BoundVariant.IgnoreOptionalIncomingTravel && _vEff[v] <= 0) continue;
                _mark[v] = _stamp;
            }

            var t = timeLeft;
            foreach (var v in _byDensity)
            {
                if (_mark[v] != _stamp) continue;
                if (t <= 0) break;
                if (_weight[v] <= t) { ubTime += _vEff[v]; t -= _weight[v]; }
                else { ubTime += _vEff[v] * t / _weight[v] + 1; break; }
            }

            var s = slots;
            if (_variant == BoundVariant.GreedySlotsOnly)
            {
                foreach (var v in _byDensity)
                {
                    if (s <= 0) break;
                    if (_mark[v] != _stamp) continue;
                    ubSlots += _vEff[v]; s--;
                }
            }
            else
            {
                foreach (var v in _byValue)
                {
                    if (s <= 0) break;
                    if (_mark[v] != _stamp) continue;
                    ubSlots += _vEff[v]; s--;
                }
            }
        }
        else if (_variant == BoundVariant.PerCallSuffix)
        {
            var candCount = 0;
            for (var i = 0; i < domainCount; i++)
            {
                var v = domain[i];
                finalReturn = Math.Min(finalReturn, _pre.ReturnRaw[v]);
                var node = _ctx.Nodes[v];
                if (node.IsMandatory) continue;
                if (budgetLeft is { } left && node.Cost > left) continue;

                var factor = _inputs.TimeFit.MaxFactorFrom(v, departure);
                if (_variant == BoundVariant.ApplyGamma)
                {
                    factor *= _inputs.P.GammaLoved;
                }
                var uMaxAt = (long)Math.Ceiling(WeightedObjectiveInputs.Scale * _inputs.Base[v] * factor);
                var minIncomingCost = (long)Math.Floor(WeightedObjectiveInputs.Scale * _inputs.P.LambdaPerMin * _pre.MinIncomingRaw[v]);
                var vEff = uMaxAt - minIncomingCost;

                if (vEff <= 0) continue;

                _candNodes[candCount] = v;
                _candVal[candCount] = vEff;
                _candDensity[candCount] = (double)vEff / Math.Max(1, _weight[v]);
                _slotsOrder[candCount] = candCount;
                _timeOrder[candCount] = candCount;
                candCount++;
            }

            if (candCount > 0)
            {
                if (_variant == BoundVariant.GreedySlotsOnly)
                {
                    // Bẫy GreedySlotsOnly: Sắp theo mật độ thay vì giá trị
                    for (var i = 1; i < candCount; i++)
                    {
                        var key = _slotsOrder[i];
                        var density = _candDensity[key];
                        var j = i - 1;
                        while (j >= 0 && _candDensity[_slotsOrder[j]] < density)
                        {
                            _slotsOrder[j + 1] = _slotsOrder[j];
                            j--;
                        }
                        _slotsOrder[j + 1] = key;
                    }

                    var takeSlots = Math.Min(slots, candCount);
                    for (var i = 0; i < takeSlots; i++)
                    {
                        ubSlots += _candVal[_slotsOrder[i]];
                    }

                    ubTime = ubSlots;
                }
                else
                {
                    // UB_slots: Duyệt đủ danh sách, lấy đúng slots giá trị G2 lớn nhất
                    for (var i = 1; i < candCount; i++)
                    {
                        var key = _slotsOrder[i];
                        var val = _candVal[key];
                        var j = i - 1;
                        while (j >= 0 && _candVal[_slotsOrder[j]] < val)
                        {
                            _slotsOrder[j + 1] = _slotsOrder[j];
                            j--;
                        }
                        _slotsOrder[j + 1] = key;
                    }

                    var takeSlots = Math.Min(slots, candCount);
                    for (var i = 0; i < takeSlots; i++)
                    {
                        ubSlots += _candVal[_slotsOrder[i]];
                    }

                    // UB_time: Knapsack phân số theo mật độ động G2
                    for (var i = 1; i < candCount; i++)
                    {
                        var key = _timeOrder[i];
                        var density = _candDensity[key];
                        var j = i - 1;
                        while (j >= 0 && _candDensity[_timeOrder[j]] < density)
                        {
                            _timeOrder[j + 1] = _timeOrder[j];
                            j--;
                        }
                        _timeOrder[j + 1] = key;
                    }

                    var t = timeLeft;
                    for (var i = 0; i < candCount; i++)
                    {
                        if (t <= 0) break;
                        var idx = _timeOrder[i];
                        var nodeIdx = _candNodes[idx];
                        var w = _weight[nodeIdx];
                        var val = _candVal[idx];
                        if (w <= t)
                        {
                            ubTime += val;
                            t -= w;
                        }
                        else
                        {
                            ubTime += val * t / w + 1;
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            var n = _ctx.Nodes.Count;
            _stamp++;
            for (var i = 0; i < domainCount; i++)
            {
                var v = domain[i];
                finalReturn = Math.Min(finalReturn, _pre.ReturnRaw[v]);
                var node = _ctx.Nodes[v];
                if (node.IsMandatory) continue;
                if (budgetLeft is { } left && node.Cost > left) continue;
                if (_vEffB[bOffset + v] <= 0) continue;
                _mark[v] = _stamp;
            }

            var t = timeLeft;
            for (var i = 0; i < n; i++)
            {
                var v = _byDensityB[bOffset + i];
                if (_mark[v] != _stamp) continue;
                if (t <= 0) break;
                var w = _weight[v];
                var val = _vEffB[bOffset + v];
                if (w <= t)
                {
                    ubTime += val;
                    t -= w;
                }
                else
                {
                    ubTime += val * t / w + 1;
                    break;
                }
            }

            var s = slots;
            if (_variant == BoundVariant.GreedySlotsOnly)
            {
                for (var i = 0; i < n; i++)
                {
                    if (s <= 0) break;
                    var v = _byDensityB[bOffset + i];
                    if (_mark[v] != _stamp) continue;
                    ubSlots += _vEffB[bOffset + v];
                    s--;
                }
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    if (s <= 0) break;
                    var v = _byValueB[bOffset + i];
                    if (_mark[v] != _stamp) continue;
                    ubSlots += _vEffB[bOffset + v];
                    s--;
                }
            }
        }

        long travelLb = (long)Math.Floor(WeightedObjectiveInputs.Scale * _inputs.P.LambdaPerMin * (forcedTravel + finalReturn));
        long slack = _variant == BoundVariant.NoRoundingSlack
            ? 0
            : (long)remainingMandatory + slots + 1; // phần đệm làm tròn

        long optionalGain = _variant == BoundVariant.GreedySlotsOnly ? ubSlots : Math.Min(ubTime, ubSlots);
        long ub = _obj[depth] + mandatoryGain + optionalGain - travelLb + slack;

        LastCanImproveDebug = $"obj={_obj[depth]}, mandGain={mandatoryGain}, optGain={optionalGain}, ubTime={ubTime}, ubSlots={ubSlots}, travelLb={travelLb}, slack={slack}, ub={ub}, inc={_incumbent?.Objective}, timeLeft={timeLeft}";

        if (ub < _incumbent!.Objective) return false; // < : giữ nhánh hòa cho tie-break
        return true;
    }

    internal string LastCanImproveDebug { get; private set; } = string.Empty;

    public long ValueScore(int value, int start, int current, int slack)
    {
        var node = _ctx.Nodes[value];
        var travelRaw = _ctx.RawTravel(current, node.MatrixIndex);
        var end = start + node.VisitMinutes;
        var cat = _inputs.CategoryIndex[value];
        var catCount = _categoryCount[cat];

        var gamma = _inputs.GammaScaled[cat][catCount] / (double)WeightedObjectiveInputs.Scale;
        var gain = _inputs.Base[value] * _inputs.TimeFit.Factor(value, start, end) * gamma;
        var travelCost = _inputs.P.LambdaPerMin * travelRaw;

        var scaledNet = (long)Math.Round(WeightedObjectiveInputs.Scale * (gain - travelCost));
        var mandatoryBonus = node.IsMandatory ? (long)_pre.MandatoryWeight * WeightedObjectiveInputs.Scale : 0;

        return -(scaledNet + mandatoryBonus);
    }

    public EliteKey PeekEliteKey(int depth, int departure, int current, int travel, int finish)
    {
        var j = ModelObjective(depth, current);
        return new EliteKey((double)j / WeightedObjectiveInputs.Scale, travel, finish);
    }

    public EliteKey SnapshotEliteKey(int depth, int departure, int current, int travel, int finish)
    {
        return PeekEliteKey(depth, departure, current, travel, finish);
    }

    public int CompareToIncumbent(int depth, int departure, int current, int travel)
    {
        _pendingDepth = depth;
        _pendingObjective = ModelObjective(depth, current); // J MÔ HÌNH
        _pendingTravel = travel;
        _pendingFinish = departure + _ctx.ReturnTravel(current);
        _pendingComparison = _incumbent is null ? -1 : ComparePendingToIncumbent();
        return _pendingComparison;
    }

    public void AcceptIncumbent(int depth, int departure, int current, int travel)
    {
        var j = ModelObjective(depth, current);
        var finish = departure + _ctx.ReturnTravel(current);
        _incumbent = WeightedIncumbent.From(j, travel, finish, _buffers.Sequence, depth, _ctx);
    }

    /// <summary>
    /// Khi có rest stop: Evaluator chỉ xác nhận lịch khả thi.
    /// Incumbent TUYỆT ĐỐI không dùng J thật từ Evaluator, mà dùng _pendingComparison từ J mô hình.
    /// </summary>
    public int CompareEvaluated(EvaluatedItinerarySchedule schedule) => _pendingComparison;

    public void AcceptEvaluated(EvaluatedItinerarySchedule schedule)
    {
        Invariant.Check(_pendingDepth == _buffers.Depth, "Có lời gọi kernel xen giữa CompareToIncumbent và AcceptEvaluated");
        _incumbent = WeightedIncumbent.From(_pendingObjective, _pendingTravel, _pendingFinish, _buffers.Sequence, _pendingDepth, _ctx);
    }

    private long ModelObjective(int depth, int currentMatrix) =>
        _obj[depth] + ObjectiveCalculator.ReturnTerm(_inputs, _ctx.RawTravel(currentMatrix, _ctx.EndMatrixIndex));

    private int ComparePendingToIncumbent()
    {
        // Cấp 1: Objective cao hơn là tốt hơn (trả về số âm nếu pending tốt hơn incumbent)
        var c = _incumbent!.Objective.CompareTo(_pendingObjective);
        if (c != 0)
        {
            return c;
        }

        // Cấp 2: Travel ít hơn là tốt hơn
        c = _pendingTravel.CompareTo(_incumbent.Travel);
        if (c != 0)
        {
            return c;
        }

        // Cấp 3: Finish sớm hơn là tốt hơn
        c = _pendingFinish.CompareTo(_incumbent.Finish);
        if (c != 0)
        {
            return c;
        }

        // Cấp 4: Dãy Id POI để đảm bảo tính tất định và đồng bộ với WeightedEliteOrdering
        return ComparePoiIdSequence(_buffers.Sequence, _pendingDepth, _incumbent.VisitPoiIds);
    }

    private int ComparePoiIdSequence(int[] sequence, int depth, long[] incumbentIds)
    {
        var minLen = Math.Min(depth, incumbentIds.Length);
        for (var i = 0; i < minLen; i++)
        {
            var candidateId = _ctx.Nodes[sequence[i]].Candidate.Id;
            var cmp = candidateId.CompareTo(incumbentIds[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return depth.CompareTo(incumbentIds.Length);
    }

    private bool TryMapToNodes(long[] poiIds, out int[] nodes)
    {
        nodes = new int[poiIds.Length];
        for (var i = 0; i < poiIds.Length; i++)
        {
            if (!_nodeOf.TryGetValue(poiIds[i], out nodes[i]))
            {
                return false;
            }
        }

        return true;
    }
}
