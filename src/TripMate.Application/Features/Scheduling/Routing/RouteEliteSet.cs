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
        // Kiểm tra mục tiêu trước khi dựng khóa: phần lớn lời giải bị loại ở đây, khỏi tạo chuỗi.
        var full = _items.Count >= capacity;
        if (full && _items.Keys.Last().Objective <= state.Objective)
        {
            return;
        }

        var key = state.Key;
        if (_keys.Contains(key))
        {
            return;
        }

        if (full)
        {
            var worst = _items.Keys.Last();
            _items.Remove(worst);
            _keys.Remove(worst.Key);
        }

        _items[(state.Objective, key)] = state;
        _keys.Add(key);
    }
}