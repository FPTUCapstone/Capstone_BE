namespace TripMate.Application.Features.Scheduling.Routing;

/// <summary>
/// Một lộ trình khả thi cùng hai mảng lan truyền khung giờ (kỹ thuật 1):
/// <list type="bullet">
/// <item><c>Earliest[i]</c>: giờ bắt đầu tham quan sớm nhất tại vị trí i, tính xuôi từ điểm xuất phát.</item>
/// <item><c>Latest[i]</c>: giờ bắt đầu muộn nhất tại vị trí i mà mọi điểm phía sau vẫn kịp giờ
/// đóng cửa và kịp về đích, tính ngược từ điểm kết thúc.</item>
/// </list>
/// Nhờ hai mảng này, <see cref="CanInsert"/> và <see cref="CanRemove"/> chỉ cần vài phép tính
/// thay vì mô phỏng lại cả lộ trình. Đối tượng bất biến: mỗi nước đi tạo một trạng thái mới.
/// </summary>
public sealed class RouteState
{
    private readonly RoutingContext _ctx;
    private readonly int[] _sequence;
    private readonly int[] _earliest;
    private readonly int[] _latest;
    private readonly bool[] _contains;

    private RouteState(
        RoutingContext ctx,
        int[] sequence,
        int[] earliest,
        int[] latest,
        bool[] contains,
        int travelMinutes,
        decimal cost,
        int finish,
        int visitedOptionalPenalty,
        int mandatoryCount)
    {
        _ctx = ctx;
        _sequence = sequence;
        _earliest = earliest;
        _latest = latest;
        _contains = contains;
        TravelMinutes = travelMinutes;
        Cost = cost;
        Finish = finish;
        VisitedOptionalPenalty = visitedOptionalPenalty;
        MandatoryCount = mandatoryCount;
    }

    public IReadOnlyList<int> Sequence => _sequence;

    public IReadOnlyList<int> Earliest => _earliest;

    public IReadOnlyList<int> Latest => _latest;

    public int Count => _sequence.Length;

    /// <summary>Tổng phút di chuyển thô theo ma trận, gồm cả chặng từ điểm xuất phát và về đích.</summary>
    public int TravelMinutes { get; }

    public decimal Cost { get; }

    /// <summary>Mốc phút về tới điểm kết thúc.</summary>
    public int Finish { get; }

    public int VisitedOptionalPenalty { get; }

    public int MandatoryCount { get; }

    public bool HasAllMandatory => MandatoryCount == _ctx.MandatoryNodes.Count;

    /// <summary>
    /// Hàm mục tiêu kiểu disjunction (kỹ thuật 2): phút di chuyển + phạt của mọi điểm tùy chọn
    /// bị bỏ qua. Càng nhỏ càng tốt.
    /// </summary>
    public long Objective => TravelMinutes + (long)(_ctx.TotalOptionalPenalty - VisitedOptionalPenalty);

    public bool Contains(int node) => _contains[node];

    public string Key => string.Join(',', _sequence);

    /// <summary>
    /// Mô phỏng một thứ tự điểm đến bằng số nguyên phút (O(n)), trả về null nếu vi phạm khung giờ,
    /// ngân sách, thời gian cho phép hoặc lặp điểm.
    /// </summary>
    public static RouteState? Build(RoutingContext ctx, IReadOnlyList<int> sequence)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(sequence);

        var count = sequence.Count;
        var order = new int[count];
        var earliest = new int[count];
        var latest = new int[count];
        var contains = new bool[ctx.Nodes.Count];
        var time = 0;
        var travel = 0;
        var cost = 0m;
        var penalty = 0;
        var mandatoryCount = 0;
        var previous = ctx.StartMatrixIndex;

        for (var i = 0; i < count; i++)
        {
            var nodeIndex = sequence[i];
            if (contains[nodeIndex])
            {
                return null;
            }

            contains[nodeIndex] = true;
            order[i] = nodeIndex;
            var node = ctx.Nodes[nodeIndex];

            travel += ctx.RawTravel(previous, node.MatrixIndex);
            var start = ctx.StartAt(nodeIndex, time + ctx.Travel(previous, node.MatrixIndex));
            if (start == RoutingContext.Infeasible)
            {
                return null;
            }

            cost += node.Cost;
            if (ctx.Budget is { } budget && cost > budget)
            {
                return null;
            }

            earliest[i] = start;
            time = start + node.VisitMinutes;
            previous = node.MatrixIndex;

            if (node.IsMandatory)
            {
                mandatoryCount++;
            }
            else
            {
                penalty += node.SkipPenalty;
            }
        }

        travel += ctx.RawTravel(previous, ctx.EndMatrixIndex);
        var finish = time + ctx.ReturnTravel(previous);
        if (finish > ctx.Horizon)
        {
            return null;
        }

        // Lượt ngược: giờ rời muộn nhất ở điểm cuối, rồi lùi dần về đầu lộ trình.
        var latestDeparture = ctx.Horizon - ctx.ReturnTravel(previous);
        for (var i = count - 1; i >= 0; i--)
        {
            var node = ctx.Nodes[order[i]];
            var latestStart = ctx.LatestStart(order[i], latestDeparture - node.VisitMinutes);
            if (latestStart == RoutingContext.NoLatestStart || latestStart < earliest[i])
            {
                return null;
            }

            latest[i] = latestStart;
            var previousMatrix = i == 0 ? ctx.StartMatrixIndex : ctx.Nodes[order[i - 1]].MatrixIndex;
            latestDeparture = latestStart - ctx.Travel(previousMatrix, node.MatrixIndex);
        }

        return new RouteState(ctx, order, earliest, latest, contains, travel, cost, finish, penalty, mandatoryCount);
    }

    /// <summary>
    /// Kiểm tra O(1): chèn <paramref name="node"/> vào trước vị trí <paramref name="position"/>
    /// (bằng <see cref="Count"/> nghĩa là cuối lộ trình) có giữ được mọi ràng buộc không.
    /// </summary>
    public bool CanInsert(int position, int node, out int deltaTravel)
    {
        deltaTravel = 0;
        if (position < 0 || position > Count || _contains[node])
        {
            return false;
        }

        var candidate = _ctx.Nodes[node];
        if (_ctx.Budget is { } budget && Cost + candidate.Cost > budget)
        {
            return false;
        }

        var previousMatrix = PreviousMatrix(position);
        var start = _ctx.StartAt(node, DepartureBefore(position) + _ctx.Travel(previousMatrix, candidate.MatrixIndex));
        if (start == RoutingContext.Infeasible)
        {
            return false;
        }

        var departure = start + candidate.VisitMinutes;
        int nextMatrix;
        if (position == Count)
        {
            if (departure + _ctx.ReturnTravel(candidate.MatrixIndex) > _ctx.Horizon)
            {
                return false;
            }

            nextMatrix = _ctx.EndMatrixIndex;
        }
        else
        {
            nextMatrix = MatrixAt(position);
            var nextStart = _ctx.StartAt(_sequence[position], departure + _ctx.Travel(candidate.MatrixIndex, nextMatrix));
            if (nextStart == RoutingContext.Infeasible || nextStart > _latest[position])
            {
                return false;
            }
        }

        deltaTravel = _ctx.RawTravel(previousMatrix, candidate.MatrixIndex)
            + _ctx.RawTravel(candidate.MatrixIndex, nextMatrix)
            - _ctx.RawTravel(previousMatrix, nextMatrix);
        return true;
    }

    /// <summary>
    /// Kiểm tra O(1): bỏ điểm ở vị trí <paramref name="position"/> có giữ được mọi ràng buộc không.
    /// Không cho bỏ điểm bắt buộc, và lộ trình phải còn ít nhất một điểm tham quan.
    /// </summary>
    public bool CanRemove(int position, out int deltaTravel)
    {
        deltaTravel = 0;
        if (position < 0 || position >= Count || Count <= 1 || _ctx.Nodes[_sequence[position]].IsMandatory)
        {
            return false;
        }

        var previousMatrix = PreviousMatrix(position);
        var departure = DepartureBefore(position);
        int nextMatrix;
        if (position == Count - 1)
        {
            if (departure + _ctx.ReturnTravel(previousMatrix) > _ctx.Horizon)
            {
                return false;
            }

            nextMatrix = _ctx.EndMatrixIndex;
        }
        else
        {
            nextMatrix = MatrixAt(position + 1);
            var nextStart = _ctx.StartAt(_sequence[position + 1], departure + _ctx.Travel(previousMatrix, nextMatrix));
            if (nextStart == RoutingContext.Infeasible || nextStart > _latest[position + 1])
            {
                return false;
            }
        }

        var removedMatrix = MatrixAt(position);
        deltaTravel = _ctx.RawTravel(previousMatrix, nextMatrix)
            - _ctx.RawTravel(previousMatrix, removedMatrix)
            - _ctx.RawTravel(removedMatrix, nextMatrix);
        return true;
    }

    public int[] WithInserted(int position, int node)
    {
        var result = new int[Count + 1];
        Array.Copy(_sequence, 0, result, 0, position);
        result[position] = node;
        Array.Copy(_sequence, position, result, position + 1, Count - position);
        return result;
    }

    public int[] WithRemoved(int position)
    {
        var result = new int[Count - 1];
        Array.Copy(_sequence, 0, result, 0, position);
        Array.Copy(_sequence, position + 1, result, position, Count - position - 1);
        return result;
    }

    /// <summary>Các cạnh của lộ trình theo chỉ số ma trận, gồm cả chặng đầu và chặng về đích.</summary>
    public IEnumerable<(int From, int To)> Arcs()
    {
        var previous = _ctx.StartMatrixIndex;
        foreach (var node in _sequence)
        {
            var current = _ctx.Nodes[node].MatrixIndex;
            yield return (previous, current);
            previous = current;
        }

        yield return (previous, _ctx.EndMatrixIndex);
    }

    private int MatrixAt(int position) => _ctx.Nodes[_sequence[position]].MatrixIndex;

    private int PreviousMatrix(int position) => position == 0 ? _ctx.StartMatrixIndex : MatrixAt(position - 1);

    private int DepartureBefore(int position) =>
        position == 0 ? 0 : _earliest[position - 1] + _ctx.Nodes[_sequence[position - 1]].VisitMinutes;
}