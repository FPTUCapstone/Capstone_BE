using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// K lời giải tốt nhất theo hàm mục tiêu trên mô hình, không trùng nhau.
/// Được tổng quát hóa để nhận <see cref="IEliteOrdering"/> thay vì gắn cứng vào product rule.
/// </summary>
internal sealed class EliteSet(IEliteOrdering ordering, int capacity)
{
    private readonly List<(RouteState State, EliteKey Key)> _items = [];

    public RouteState? Best => _items.Count == 0 ? null : _items[0].State;

    public IEnumerable<RouteState> Ordered => _items.Select(item => item.State);

    public int Count => _items.Count;

    /// <summary>
    /// Kiểm tra rẻ trước khi dựng lời giải: false khi tập đã đầy và lời giải chắc chắn kém phần tử cuối.
    /// Hòa thì vẫn trả về true để so tiếp ở <see cref="Offer(RouteState, in EliteKey)"/>.
    /// </summary>
    public bool MayAccept(in EliteKey key)
    {
        if (_items.Count < capacity)
        {
            return true;
        }

        return ordering.MayAccept(key, _items[^1].Key);
    }

    public void Offer(RouteState state) => Offer(state, ordering.CreateKey(state));

    public void Offer(RouteState state, in EliteKey key)
    {
        if (key.Travel != state.TravelMinutes || key.Finish != state.Finish)
        {
            throw new InvalidOperationException(
                $"EliteKey tạo trong tìm kiếm lệch với RouteState: key=({key.Travel}, {key.Finish}) vs state=({state.TravelMinutes}, {state.Finish})");
        }

        var index = 0;
        while (index < _items.Count)
        {
            var comparison = ordering.Compare(state, key, _items[index].State, _items[index].Key);
            if (comparison == 0)
            {
                // Bằng nhau nghĩa là cùng một lộ trình (trùng lặp).
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

        _items.Insert(index, (state, key));
        if (_items.Count > capacity)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }
}
