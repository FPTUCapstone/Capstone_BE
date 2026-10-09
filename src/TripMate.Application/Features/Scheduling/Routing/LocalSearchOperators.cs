namespace TripMate.Application.Features.Scheduling.Routing;

public enum RoutingMoveKind
{
    TwoOpt,
    Relocate,
    OrOpt,
    Exchange,
    InsertOptional,
    RemoveOptional,
}

/// <summary>Một nước đi đề xuất: thứ tự điểm đến mới và loại toán tử đã tạo ra nó.</summary>
public readonly record struct RoutingMove(RoutingMoveKind Kind, int[] Sequence);

/// <summary>
/// Các toán tử tìm kiếm cục bộ (kỹ thuật 3 và 4). 2-opt và Relocate giữ cùng định nghĩa với
/// <c>OptionalRouteOptimizer</c>; Or-opt, Exchange, Insert và Remove là phần bổ sung.
/// Insert và Remove được lọc trước bằng kiểm tra O(1) của <see cref="RouteState"/>; các toán tử
/// còn lại được kiểm tra bằng <see cref="RouteState.Build"/> (O(n), chỉ số nguyên).
/// </summary>
public static class LocalSearchOperators
{
    public static IEnumerable<RoutingMove> Enumerate(RoutingContext ctx, RouteState state, MiniRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        var sequence = state.Sequence;
        var count = sequence.Count;

        // 2-opt: đảo ngược đoạn [i..j].
        for (var i = 0; i < count - 1; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                yield return new RoutingMove(RoutingMoveKind.TwoOpt, Reverse(sequence, i, j));
            }
        }

        // Relocate: dời một điểm sang vị trí khác.
        for (var from = 0; from < count; from++)
        {
            for (var to = 0; to < count; to++)
            {
                if (from != to)
                {
                    yield return new RoutingMove(RoutingMoveKind.Relocate, MoveSegment(sequence, from, 1, to, reversed: false));
                }
            }
        }

        // Or-opt: dời một đoạn 2..L điểm liên tiếp sang chỗ khác, giữ hoặc đảo chiều đoạn.
        var maxLength = Math.Min(options.MaxOrOptSegmentLength, count - 1);
        for (var length = 2; length <= maxLength; length++)
        {
            for (var from = 0; from + length <= count; from++)
            {
                for (var to = 0; to <= count - length; to++)
                {
                    if (to == from)
                    {
                        continue;
                    }

                    yield return new RoutingMove(RoutingMoveKind.OrOpt, MoveSegment(sequence, from, length, to, reversed: false));
                    yield return new RoutingMove(RoutingMoveKind.OrOpt, MoveSegment(sequence, from, length, to, reversed: true));
                }
            }
        }

        // Exchange: hoán đổi hai điểm không kề nhau (kề nhau đã có trong 2-opt).
        for (var i = 0; i < count - 2; i++)
        {
            for (var j = i + 2; j < count; j++)
            {
                yield return new RoutingMove(RoutingMoveKind.Exchange, Swap(sequence, i, j));
            }
        }

        // Insert: thêm một điểm tùy chọn chưa có vào vị trí khả thi rẻ nhất của nó.
        foreach (var node in ctx.OptionalNodes)
        {
            if (state.Contains(node))
            {
                continue;
            }

            var bestPosition = -1;
            var bestDelta = int.MaxValue;
            for (var position = 0; position <= count; position++)
            {
                if (state.CanInsert(position, node, out var delta) && delta < bestDelta)
                {
                    bestDelta = delta;
                    bestPosition = position;
                }
            }

            if (bestPosition >= 0)
            {
                yield return new RoutingMove(RoutingMoveKind.InsertOptional, state.WithInserted(bestPosition, node));
            }
        }

        // Remove: bỏ một điểm tùy chọn đang có.
        for (var position = 0; position < count; position++)
        {
            if (state.CanRemove(position, out _))
            {
                yield return new RoutingMove(RoutingMoveKind.RemoveOptional, state.WithRemoved(position));
            }
        }
    }

    internal static int[] Reverse(IReadOnlyList<int> sequence, int start, int end)
    {
        var result = sequence.ToArray();
        Array.Reverse(result, start, end - start + 1);
        return result;
    }

    internal static int[] Swap(IReadOnlyList<int> sequence, int i, int j)
    {
        var result = sequence.ToArray();
        (result[i], result[j]) = (result[j], result[i]);
        return result;
    }

    /// <summary>
    /// Cắt đoạn <paramref name="length"/> điểm bắt đầu tại <paramref name="from"/>, rồi chèn lại
    /// sao cho điểm đầu của đoạn đứng ở vị trí <paramref name="to"/> trong lộ trình mới.
    /// </summary>
    internal static int[] MoveSegment(IReadOnlyList<int> sequence, int from, int length, int to, bool reversed)
    {
        var segment = new int[length];
        for (var k = 0; k < length; k++)
        {
            segment[k] = sequence[from + k];
        }

        if (reversed)
        {
            Array.Reverse(segment);
        }

        var rest = new List<int>(sequence.Count - length);
        for (var k = 0; k < sequence.Count; k++)
        {
            if (k < from || k >= from + length)
            {
                rest.Add(sequence[k]);
            }
        }

        rest.InsertRange(to, segment);
        return rest.ToArray();
    }
}