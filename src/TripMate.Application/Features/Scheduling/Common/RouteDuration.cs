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

    private RouteDurationMatrix(int[,] minutes)
    {
        _minutes = minutes;
    }

    public int PointCount => _minutes.GetLength(0);

    public static RouteDurationMatrix Create(int[,] minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.GetLength(0) == 0 || minutes.GetLength(0) != minutes.GetLength(1))
        {
            throw new ArgumentException("Route duration matrix must be non-empty and square.", nameof(minutes));
        }

        for (var row = 0; row < minutes.GetLength(0); row++)
        {
            for (var column = 0; column < minutes.GetLength(1); column++)
            {
                if (minutes[row, column] < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(minutes));
                }
            }
        }

        return new RouteDurationMatrix((int[,])minutes.Clone());
    }

    public int GetMinutes(int fromIndex, int toIndex) => _minutes[fromIndex, toIndex];
}
