namespace TripMate.Application.Features.Scheduling.Routing;

/// <summary>
/// Giữ K lời giải tốt nhất, không trùng thứ tự điểm đến, xếp theo <see cref="RouteState.Objective"/>
/// rồi theo khóa để kết quả luôn ổn định giữa các lần chạy.
/// </summary>
internal sealed class RouteEliteSet(int capacity)
{
    private readonly SortedDictionary<(long Objective, string Key), RouteState> _items = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    public RouteState? Best => _items.Count == 0 ? null : _items.First().Value;

    public IReadOnlyList<RouteState> Ordered => _items.Values.ToArray();

    public void Offer(RouteState state)
    {
        var key = state.Key;
        if (_keys.Contains(key))
        {
            return;
        }

        if (_items.Count >= capacity)
        {
            var worst = _items.Keys.Last();
            if (worst.Objective <= state.Objective)
            {
                return;
            }

            _items.Remove(worst);
            _keys.Remove(worst.Key);
        }

        _items[(state.Objective, key)] = state;
        _keys.Add(key);
    }
}