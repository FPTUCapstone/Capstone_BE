namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Khóa đại diện cho chất lượng của một lời giải ưu tú trong <see cref="EliteSet"/>.
/// Hỗ trợ cả chế độ từ điển (Lexicographic - lưu <see cref="Inclusion"/>) và chế độ trọng số (Weighted - lưu <see cref="Utility"/>).
/// Là struct giá trị nhỏ gọn, không cấp phát heap khi so sánh trước (MayAccept).
/// </summary>
internal readonly struct EliteKey : IEquatable<EliteKey>
{
    public readonly ulong[]? Inclusion;
    public readonly double Utility;
    public readonly int Travel;
    public readonly int Finish;

    public EliteKey(ulong[] inclusion, int travel, int finish)
    {
        Inclusion = inclusion;
        Utility = 0;
        Travel = travel;
        Finish = finish;
    }

    public EliteKey(double utility, int travel, int finish)
    {
        Inclusion = null;
        Utility = utility;
        Travel = travel;
        Finish = finish;
    }

    public bool Equals(EliteKey other) =>
        Travel == other.Travel &&
        Finish == other.Finish &&
        Utility.Equals(other.Utility) &&
        ReferenceEquals(Inclusion, other.Inclusion);

    public override bool Equals(object? obj) => obj is EliteKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Inclusion, Utility, Travel, Finish);
}