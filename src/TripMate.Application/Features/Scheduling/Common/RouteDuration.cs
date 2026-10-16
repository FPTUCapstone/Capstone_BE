namespace TripMate.Application.Features.Scheduling.Common;

public sealed record RoutePoint
{
    public RoutePoint(decimal latitude, decimal longitude)
    {
        if (latitude is < -90m or > 90m)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (longitude is < -180m or > 180m)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public decimal Latitude { get; }

    public decimal Longitude { get; }
}

public sealed class RouteDurationMatrix
{
    private readonly int[,] _minutes;
    private readonly bool[,]? _unreachable;
    private readonly int[,]? _meters;

    private RouteDurationMatrix(int[,] minutes, bool[,]? unreachable, int[,]? meters = null)
    {
        _minutes = minutes;
        _unreachable = unreachable;
        _meters = meters;
    }

    public int PointCount => _minutes.GetLength(0);

    /// <summary>True when the provider found no route for at least one pair of points.</summary>
    public bool HasUnreachablePairs => _unreachable is not null;

    public static RouteDurationMatrix Create(int[,] minutes) => Create(minutes, unreachable: null, meters: null);

    public static RouteDurationMatrix Create(int[,] minutes, bool[,]? unreachable) => Create(minutes, unreachable, meters: null);

    /// <summary>
    /// Creates a matrix where <paramref name="unreachable"/> marks pairs the provider could not route
    /// (for example a mountain-top POI with no road). Their minutes are ignored.
    /// </summary>
    public static RouteDurationMatrix Create(int[,] minutes, bool[,]? unreachable, int[,]? meters)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.GetLength(0) == 0 || minutes.GetLength(0) != minutes.GetLength(1))
        {
            throw new ArgumentException("Route duration matrix must be non-empty and square.", nameof(minutes));
        }

        if (unreachable is not null
            && (unreachable.GetLength(0) != minutes.GetLength(0) || unreachable.GetLength(1) != minutes.GetLength(1)))
        {
            throw new ArgumentException("Unreachable pairs must match the matrix size.", nameof(unreachable));
        }

        if (meters is not null
            && (meters.GetLength(0) != minutes.GetLength(0) || meters.GetLength(1) != minutes.GetLength(1)))
        {
            throw new ArgumentException("Meters matrix must match the matrix size.", nameof(meters));
        }

        var hasUnreachablePair = false;
        for (var row = 0; row < minutes.GetLength(0); row++)
        {
            for (var column = 0; column < minutes.GetLength(1); column++)
            {
                if (unreachable?[row, column] == true)
                {
                    hasUnreachablePair = true;
                    continue;
                }

                if (minutes[row, column] < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(minutes));
                }

                if (meters is not null && meters[row, column] < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(meters));
                }
            }
        }

        return new RouteDurationMatrix(
            (int[,])minutes.Clone(),
            hasUnreachablePair ? (bool[,])unreachable!.Clone() : null,
            meters is not null ? (int[,])meters.Clone() : null);
    }

    public bool IsReachable(int fromIndex, int toIndex) => _unreachable?[fromIndex, toIndex] != true;

    public int GetMinutes(int fromIndex, int toIndex) => IsReachable(fromIndex, toIndex)
        ? _minutes[fromIndex, toIndex]
        : throw new InvalidOperationException($"No route between points {fromIndex} and {toIndex}.");

    public int? GetMeters(int fromIndex, int toIndex)
    {
        if (!IsReachable(fromIndex, toIndex))
        {
            return null;
        }

        return _meters?[fromIndex, toIndex];
    }

    /// <summary>The matrix restricted to <paramref name="pointIndices"/>, in that order.</summary>
    public RouteDurationMatrix Select(IReadOnlyList<int> pointIndices)
    {
        ArgumentNullException.ThrowIfNull(pointIndices);
        var size = pointIndices.Count;
        var minutes = new int[size, size];
        var unreachable = new bool[size, size];
        var meters = _meters is not null ? new int[size, size] : null;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                minutes[row, column] = _minutes[pointIndices[row], pointIndices[column]];
                unreachable[row, column] = !IsReachable(pointIndices[row], pointIndices[column]);
                if (meters is not null)
                {
                    meters[row, column] = _meters![pointIndices[row], pointIndices[column]];
                }
            }
        }

        return Create(minutes, unreachable, meters);
    }
}